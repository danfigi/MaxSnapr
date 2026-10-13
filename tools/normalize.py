import pathlib, sys
root=pathlib.Path(sys.argv[1])
for p in root.rglob('*.cs'):
    if 'obj' in p.parts or 'bin' in p.parts: continue
    s=p.read_text(encoding='utf-8')
    out=''.join((chr(92)+'u%04X'%ord(c)) if 0xE000<=ord(c)<=0xF8FF else c for c in s)
    if out!=s:
        p.write_text(out,encoding='utf-8'); print('normalized',p.name)
