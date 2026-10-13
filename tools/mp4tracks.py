"""Lists the tracks of an MP4 (handler, codec, duration) by walking its boxes; never decodes media."""
import struct, sys

def boxes(data, start, end):
    i = start
    while i + 8 <= end:
        size, kind = struct.unpack('>I4s', data[i:i + 8])
        hdr = 8
        if size == 1:
            size = struct.unpack('>Q', data[i + 8:i + 16])[0]; hdr = 16
        elif size == 0:
            size = end - i
        yield kind.decode('latin1'), i + hdr, i + size
        i += size

def find(data, start, end, path):
    for k, s, e in boxes(data, start, end):
        if k == path[0]:
            if len(path) == 1:
                yield s, e
            else:
                yield from find(data, s, e, path[1:])

data = open(sys.argv[1], 'rb').read()
for ms, me in find(data, 0, len(data), ['moov']):
    for ts, te in find(data, ms, me, ['trak']):
        handler = codec = '?'
        dur = 0.0
        for hs, he in find(data, ts, te, ['mdia', 'hdlr']):
            handler = data[hs + 8:hs + 12].decode('latin1')
        for hs, he in find(data, ts, te, ['mdia', 'mdhd']):
            v = data[hs]
            if v == 1:
                scale, d = struct.unpack('>IQ', data[hs + 20:hs + 32])
            else:
                scale, d = struct.unpack('>II', data[hs + 12:hs + 20])
            dur = d / scale if scale else 0
        for ss, se in find(data, ts, te, ['mdia', 'minf', 'stbl', 'stsd']):
            codec = data[ss + 12:ss + 16].decode('latin1')
        print(f'{handler} {codec} {dur:.2f}s')
