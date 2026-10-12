"""Generate independent codec fixtures with Pillow and ImageMagick; never uses this library."""
from pathlib import Path
import json
import struct
import subprocess
import tempfile
import zlib
from PIL import Image, __version__ as pillow_version

root = Path(__file__).resolve().parents[1] / 'tests' / 'Dijitalya.Imaging.Tests' / 'Fixtures'
root.mkdir(parents=True, exist_ok=True)
width, height = 37, 29
rgb = Image.new('RGB', (width, height))
rgb.putdata([((x * 5 + y * 2) % 256, (x * 2 + y * 6) % 256, (x * 3 + y * 3) % 256) for y in range(height) for x in range(width)])
manifest = []

def register(name, tolerance=0, reference=None):
    picture = (reference or Image.open(root / name)).convert('RGBA')
    if name.endswith('.gif'):
        pixels = bytearray(picture.tobytes())
        for offset in range(0, len(pixels), 4):
            if pixels[offset + 3] == 0:
                pixels[offset:offset + 3] = b'\0\0\0'
        picture = Image.frombytes('RGBA', picture.size, bytes(pixels))
    expected = name + '.rgba'
    (root / expected).write_bytes(picture.tobytes())
    manifest.append({'File': name, 'Expected': expected, 'Width': picture.width, 'Height': picture.height, 'MeanAbsoluteError': tolerance})

for name, mode, progressive, subsampling in [('baseline.jpg','RGB',False,0), ('progressive.jpg','RGB',True,0), ('subsampled.jpg','RGB',False,2), ('grayscale.jpg','L',False,0), ('cmyk.jpg','CMYK',False,0)]:
    rgb.convert(mode).save(root/name, quality=92, progressive=progressive, subsampling=subsampling)
    register(name, 3.0)
rgba = rgb.convert('RGBA')
rgba.putalpha(Image.frombytes('L', (width,height), bytes((x*7+y*3)%256 for y in range(height) for x in range(width))))
rgba.save(root/'rgba.png'); register('rgba.png')
rgba.save(root/'lossless.webp', lossless=True, exact=True); register('lossless.webp')
palette = rgb.quantize(colors=16)
palette.save(root/'indexed.png', bits=4); register('indexed.png')
palette.save(root/'palette.gif', transparency=0); register('palette.gif')
rgb.convert('1').save(root/'monochrome.png'); register('monochrome.png')
rgb.save(root/'rgb.bmp'); register('rgb.bmp')
# ImageMagick performs Adam7 interlacing; verify the IHDR interlace flag below.
subprocess.run(['convert', str(root/'rgba.png'), '-interlace', 'PNG', 'PNG32:'+str(root/'adam7.png')],check=True)
assert (root/'adam7.png').read_bytes()[28] == 1
register('adam7.png')
# Construct 16-bit grayscale PNG independently to pin the documented high-byte conversion.
def chunk(kind, body):
    return struct.pack('>I',len(body))+kind+body+struct.pack('>I',zlib.crc32(kind+body)&0xffffffff)
values=[(x*1231+y*719)%65536 for y in range(height) for x in range(width)]
raw=b''.join(b'\0'+b''.join(struct.pack('>H',v) for v in values[y*width:(y+1)*width]) for y in range(height))
(root/'grayscale16.png').write_bytes(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',width,height,16,0,0,0,0))+chunk(b'IDAT',zlib.compress(raw))+chunk(b'IEND',b''))
expected=Image.frombytes('L',(width,height),bytes(v>>8 for v in values)); register('grayscale16.png', reference=expected)
# Two distinct run-length formats with encoded, absolute, EOL and EOB commands.
colors=[(0,0,0),(255,0,0),(0,255,0),(0,0,255)]
for bits, compression, encoded in [(8,1,bytes([4,1,0,0,0,4,2,3,2,3,0,0,0,1])),(4,2,bytes([4,0x11,0,0,0,4,0x23,0x23,0,0,0,1]))]:
    palette_bytes=b''.join(bytes([b,g,r,0]) for r,g,b in colors)
    offset=14+40+len(palette_bytes)
    dib=struct.pack('<IiiHHIIiiII',40,4,2,1,bits,compression,len(encoded),0,0,4,0)
    name=f'rle{bits}.bmp'
    (root/name).write_bytes(struct.pack('<2sIHHI',b'BM',offset+len(encoded),0,0,offset)+dib+palette_bytes+encoded)
    expected=Image.new('RGB',(4,2)); expected.putdata([colors[i] for i in [2,3,2,3,1,1,1,1]])
    # Cross-check the independently constructed RLE fixture using ImageMagick.
    rendered=subprocess.run(['convert',str(root/name),'-depth','8','rgba:-'],check=True,capture_output=True).stdout
    assert rendered==expected.convert('RGBA').tobytes()
    register(name,reference=expected)
(root/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
(root/'README.md').write_text(f'''# Independent codec fixtures

Generated with Pillow {pillow_version} and ImageMagick by `tools/generate_fixtures.py`.
Synthetic images are created by this repository; no external photographs are included.
The `.rgba` files contain independent expected pixels. JPEG comparisons allow a mean
absolute channel difference of 3 because rounding and chroma interpolation can differ.
Lossless comparisons are exact; invisible GIF palette colors are normalized to transparent black.
The grayscale16 fixture follows this library's documented
high-byte conversion. RLE fixtures are also cross-checked with ImageMagick.
These fixtures are regression coverage, not proof of complete format conformance.
''')
print(f'Generated {len(manifest)} independent fixtures.')
