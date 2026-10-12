"""Check the library's exported files with Pillow's independent native decoders."""
from pathlib import Path
import sys
from PIL import Image

folder = Path(sys.argv[1])
expected = (folder / 'source.rgba').read_bytes()
for extension in ['jpg', 'png', 'webp', 'gif', 'bmp']:
    image = Image.open(folder / f'image.{extension}').convert('RGBA')
    assert image.size == (256, 192), extension
    actual = image.tobytes()
    error = sum(abs(a-b) for a,b in zip(actual, expected)) / len(expected)
    limit = 0 if extension in ['png', 'bmp'] else 8
    assert error <= limit, (extension, error)
    print(f'PASS {extension}: mean absolute channel error {error:.4f}')
