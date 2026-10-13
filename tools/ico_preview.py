import struct, sys, os

src, outdir = sys.argv[1], sys.argv[2]
os.makedirs(outdir, exist_ok=True)
data = open(src, 'rb').read()
_, _, n = struct.unpack('<HHH', data[:6])
for i in range(n):
    w, h, _, _, _, _, size, off = struct.unpack('<BBBBHHII', data[6 + 16 * i:22 + 16 * i])
    w = w or 256
    open(os.path.join(outdir, f'icon-{w}.png'), 'wb').write(data[off:off + size])
    print(w, size)
