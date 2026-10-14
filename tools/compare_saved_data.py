"""Checks that two self-test homes (e.g. normal vs obfuscated build) store settings, library and .snapr documents with the same JSON keys."""
import glob, json, os, sys, zipfile

def keys(o, p=''):
    out = set()
    if isinstance(o, dict):
        for k, v in o.items():
            out.add(p + '/' + k)
            out |= keys(v, p + '/' + k)
    elif isinstance(o, list):
        for v in o:
            out |= keys(v, p + '[]')
    return out

def collect(home):
    settings = json.load(open(os.path.join(home, 'settings.json'), encoding='utf-8-sig'))
    library = json.load(open(os.path.join(home, 'Biblioteca', 'library.json'), encoding='utf-8-sig'))
    docs = glob.glob(os.path.join(home, 'Biblioteca', '**', '*.snapr'), recursive=True)
    doc_keys = set()
    for f in docs:
        with zipfile.ZipFile(f) as z:
            doc_keys |= keys(json.loads(z.read('document.json').decode('utf-8-sig')))
    return keys(settings), doc_keys, keys(library), len(docs)

a, b = collect(sys.argv[1]), collect(sys.argv[2])
ok = True
for i, label in enumerate(['ajustes', 'documentos .snapr', 'biblioteca']):
    diff = sorted(a[i] ^ b[i])
    ok &= not diff
    print(f'{label}: {len(a[i])} claves / {len(b[i])} claves, diferencias: {len(diff)} {diff[:5]}')
print(f'documentos revisados: {a[3]} / {b[3]}')
print('COMPATIBLE' if ok else 'INCOMPATIBLE')
