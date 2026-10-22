"""Fetch pinned Framework-compatible NuGet runtime assemblies from api.nuget.org.
Keeps package hashes and license material; never installs a system-wide provider.
"""
import hashlib, io, json, pathlib, urllib.request, zipfile, xml.etree.ElementTree as ET
root = pathlib.Path(__file__).resolve().parents[1] / 'DatabaseProvider'
root.mkdir(exist_ok=True)
queue = [('MySqlConnector', '2.4.0')]
selected = {}
rank = ['net462', 'net461', 'net46', 'net45', 'net40', 'netstandard2.0', 'netstandard1.3', 'netstandard1.1', 'netstandard1.0']
def tfm(value):
    return value.lower().replace('.netframework', 'net').replace('.netstandard', 'netstandard').replace('v', '').replace('.', '') if value.lower().startswith('.netframework') else value.lower().replace('.netstandard', 'netstandard')
def version(value): return tuple(int(x) for x in value.split('.'))
manifest = []
while queue:
    name, ver = queue.pop(0)
    if name in selected and version(selected[name]) >= version(ver): continue
    url = f'https://api.nuget.org/v3-flatcontainer/{name.lower()}/{ver}/{name.lower()}.{ver}.nupkg'
    data = urllib.request.urlopen(url, timeout=60).read()
    z = zipfile.ZipFile(io.BytesIO(data))
    xml = ET.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.nuspec'))))
    ns = {'n': xml.tag.split('}')[0].strip('{')}
    groups = xml.findall('.//n:dependencies/n:group', ns)
    suitable = [(rank.index(tfm(g.attrib.get('targetFramework', ''))), g) for g in groups if tfm(g.attrib.get('targetFramework', '')) in rank]
    dependencies = min(suitable, key=lambda x:x[0])[1].findall('n:dependency', ns) if suitable else xml.findall('.//n:dependencies/n:dependency', ns)
    for d in dependencies:
        v = d.attrib['version'].strip('[]()').split(',')[0]
        queue.append((d.attrib['id'], v))
    libs = [n for n in z.namelist() if n.startswith('lib/') and n.endswith('.dll') and n.split('/')[1] in rank]
    if not libs: raise RuntimeError(f'No compatible Framework runtime: {name} {ver}')
    chosen = min({n.split('/')[1] for n in libs}, key=rank.index)
    for n in libs:
        if n.split('/')[1] == chosen: (root / pathlib.PurePosixPath(n).name).write_bytes(z.read(n))
    notice = root / 'Licenses' / (name + '-' + ver)
    notice.mkdir(parents=True, exist_ok=True)
    for n in z.namelist():
        if pathlib.PurePosixPath(n).name.lower().startswith(('license', 'third-party-notices')) or n.endswith('.nuspec'):
            (notice / pathlib.PurePosixPath(n).name).write_bytes(z.read(n))
    selected[name] = ver
    manifest.append({'package':name, 'version':ver, 'url':url, 'sha256':hashlib.sha256(data).hexdigest(), 'framework':chosen})
(root / 'packages.lock.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
print('Prepared', len(selected), 'Framework provider packages')
