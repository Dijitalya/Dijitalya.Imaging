namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>
/// Decodes the entropy-coded data of one scan into the coefficients of its components: a baseline
/// scan, or one of the four kinds of progressive scan (DC first, DC refinement, AC first, AC
/// refinement) of T.81 Annex G. Restart intervals reset the predictions and the end-of-band run.
/// </summary>
internal sealed class JpegScanDecoder
{
    private readonly byte[] _data;
    private readonly JpegFrame _frame;
    private readonly IReadOnlyList<JpegComponent> _components;
    private readonly int _restartInterval;
    private readonly int _spectralStart;
    private readonly int _spectralEnd;
    private readonly int _successiveHigh;
    private readonly int _successiveLow;
    private readonly JpegBitReader _reader;

    private int _endOfBandRun;
    private int _refinementState;
    private int _refinementValue;

    public JpegScanDecoder(
        byte[] data, int position, JpegFrame frame, IReadOnlyList<JpegComponent> components, int restartInterval,
        int spectralStart, int spectralEnd, int successiveHigh, int successiveLow)
    {
        _data = data;
        _frame = frame;
        _components = components;
        _restartInterval = restartInterval;
        _spectralStart = spectralStart;
        _spectralEnd = spectralEnd;
        _successiveHigh = successiveHigh;
        _successiveLow = successiveLow;
        _reader = new JpegBitReader(data, position);
    }

    /// <summary>Decodes the scan; returns the position of the marker that follows it.</summary>
    public int Decode()
    {
        var single = _components.Count == 1;
        var mcuCount = single
            ? _components[0].BlocksPerLine * _components[0].BlocksPerColumn
            : _frame.McusPerLine * _frame.McusPerColumn;
        var interval = _restartInterval > 0 ? _restartInterval : mcuCount;
        var mcu = 0;

        while (true)
        {
            foreach (var component in _components)
                component.DcPredictor = 0;

            _endOfBandRun = 0;
            _refinementState = 0;

            var end = Math.Min(mcu + interval, mcuCount);

            for (; mcu < end; mcu++)
            {
                if (single)
                {
                    var component = _components[0];
                    DecodeBlock(component, mcu / component.BlocksPerLine, mcu % component.BlocksPerLine);
                    continue;
                }

                var mcuRow = mcu / _frame.McusPerLine;
                var mcuColumn = mcu % _frame.McusPerLine;

                foreach (var component in _components)
                {
                    for (var row = 0; row < component.VerticalFactor; row++)
                    {
                        for (var column = 0; column < component.HorizontalFactor; column++)
                        {
                            DecodeBlock(
                                component,
                                (mcuRow * component.VerticalFactor) + row,
                                (mcuColumn * component.HorizontalFactor) + column);
                        }
                    }
                }
            }

            var marker = _reader.AlignToMarker();

            if (mcu >= mcuCount || marker + 1 >= _data.Length || _data[marker + 1] is < 0xD0 or > 0xD7)
                return marker;

            _reader.SkipMarker();
        }
    }

    private void DecodeBlock(JpegComponent component, int blockRow, int blockColumn)
    {
        var offset = component.BlockOffset(blockRow, blockColumn);

        if (!_frame.Progressive)
            DecodeBaseline(component, offset);
        else if (_spectralStart == 0)
        {
            if (_successiveHigh == 0)
                DecodeDcFirst(component, offset);
            else
                DecodeDcRefinement(component, offset);
        }
        else if (_successiveHigh == 0)
        {
            DecodeAcFirst(component, offset);
        }
        else
        {
            DecodeAcRefinement(component, offset);
        }
    }

    private void DecodeBaseline(JpegComponent component, int offset)
    {
        var coefficients = component.Coefficients;
        var size = DcSize(component);

        component.DcPredictor += _reader.ReceiveExtend(size);
        coefficients[offset] = (short)component.DcPredictor;

        var table = component.AcTable!;
        var index = 1;

        while (index < 64)
        {
            var symbol = table.Decode(_reader);
            var bits = symbol & 15;
            var run = symbol >> 4;

            if (bits == 0)
            {
                if (run < 15)
                    break;

                index += 16;
                continue;
            }

            index += run;

            if (index > 63)
                break;

            coefficients[offset + JpegTables.ZigZag[index]] = (short)_reader.ReceiveExtend(bits);
            index++;
        }
    }

    private void DecodeDcFirst(JpegComponent component, int offset)
    {
        var size = DcSize(component);

        component.DcPredictor += _reader.ReceiveExtend(size) * (1 << _successiveLow);
        component.Coefficients[offset] = (short)component.DcPredictor;
    }

    private void DecodeDcRefinement(JpegComponent component, int offset)
    {
        if (_reader.ReadBit() != 0)
            component.Coefficients[offset] |= (short)(1 << _successiveLow);
    }

    private void DecodeAcFirst(JpegComponent component, int offset)
    {
        if (_endOfBandRun > 0)
        {
            _endOfBandRun--;
            return;
        }

        var coefficients = component.Coefficients;
        var table = component.AcTable!;
        var index = _spectralStart;

        while (index <= _spectralEnd)
        {
            var symbol = table.Decode(_reader);
            var bits = symbol & 15;
            var run = symbol >> 4;

            if (bits == 0)
            {
                if (run < 15)
                {
                    _endOfBandRun = _reader.ReadBits(run) + (1 << run) - 1;
                    break;
                }

                index += 16;
                continue;
            }

            index += run;

            if (index > 63)
                break;

            coefficients[offset + JpegTables.ZigZag[index]] = (short)(_reader.ReceiveExtend(bits) * (1 << _successiveLow));
            index++;
        }
    }

    /// <summary>
    /// AC refinement (T.81 G.1.2.3) as a small state machine, because a band of zeros to skip, the
    /// value to set after it and an end-of-band run can all span the coefficients that only receive a
    /// correction bit: 0 reads the next symbol, 1 and 2 skip zeros (2 then sets a value), 3 sets the
    /// value at the next zero, 4 is inside an end-of-band run.
    /// </summary>
    private void DecodeAcRefinement(JpegComponent component, int offset)
    {
        var coefficients = component.Coefficients;
        var table = component.AcTable!;
        var bit = 1 << _successiveLow;
        var index = _spectralStart;
        var run = 0;

        while (index <= _spectralEnd)
        {
            var position = offset + JpegTables.ZigZag[index];
            var coefficient = coefficients[position];
            var direction = coefficient < 0 ? -1 : 1;

            switch (_refinementState)
            {
                case 0:
                {
                    var symbol = table.Decode(_reader);
                    var bits = symbol & 15;
                    run = symbol >> 4;

                    if (bits == 0)
                    {
                        if (run < 15)
                        {
                            _endOfBandRun = _reader.ReadBits(run) + (1 << run);
                            _refinementState = 4;
                        }
                        else
                        {
                            run = 16;
                            _refinementState = 1;
                        }
                    }
                    else
                    {
                        if (bits != 1)
                            throw new InvalidImageContentException("A JPEG refinement scan is malformed.");

                        _refinementValue = _reader.ReceiveExtend(bits);
                        _refinementState = run != 0 ? 2 : 3;
                    }

                    // The symbol only sets the state; the same coefficient is visited again.
                    continue;
                }

                case 1:
                case 2:
                    if (coefficient != 0)
                    {
                        coefficients[position] += (short)(direction * _reader.ReadBit() * bit);
                    }
                    else
                    {
                        run--;

                        if (run == 0)
                            _refinementState = _refinementState == 2 ? 3 : 0;
                    }

                    break;

                case 3:
                    if (coefficient != 0)
                    {
                        coefficients[position] += (short)(direction * _reader.ReadBit() * bit);
                    }
                    else
                    {
                        coefficients[position] = (short)(_refinementValue * bit);
                        _refinementState = 0;
                    }

                    break;

                default:
                    if (coefficient != 0)
                        coefficients[position] += (short)(direction * _reader.ReadBit() * bit);

                    break;
            }

            index++;
        }

        if (_refinementState == 4)
        {
            _endOfBandRun--;

            if (_endOfBandRun == 0)
                _refinementState = 0;
        }
    }

    private int DcSize(JpegComponent component)
    {
        var size = component.DcTable!.Decode(_reader);

        if (size > 16)
            throw new InvalidImageContentException("A JPEG DC difference is out of range.");

        return size;
    }
}
