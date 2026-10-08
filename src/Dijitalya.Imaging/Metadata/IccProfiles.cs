namespace Dijitalya.Imaging.Metadata;

/// <summary>Checks on embedded ICC profiles.</summary>
internal static class IccProfiles
{
    private const int HeaderSize = 128;

    /// <summary>
    /// The profile when it describes RGB data, null otherwise. The decoders always produce RGB, so a
    /// CMYK or grey profile would describe colours the pixels no longer have.
    /// </summary>
    public static byte[]? RgbOrNull(byte[]? profile)
    {
        if (profile is null || profile.Length < HeaderSize)
            return null;

        // The data colour space signature sits at offset 16 of the profile header.
        return profile[16] == 'R' && profile[17] == 'G' && profile[18] == 'B' && profile[19] == ' ' ? profile : null;
    }
}
