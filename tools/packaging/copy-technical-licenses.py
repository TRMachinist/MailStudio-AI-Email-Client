from pathlib import Path
from importlib import metadata
import shutil,sys,json,urllib.request
target=Path(sys.argv[1]);target.mkdir(parents=True,exist_ok=True);records=[]
for distribution in metadata.distributions():
    name=distribution.metadata.get('Name','unknown');version=distribution.version
    for f in distribution.files or []:
        if any(f.name.upper().startswith(prefix) for prefix in ('LICENSE','COPYING','NOTICE','THIRDPARTY')):
            source=Path(distribution.locate_file(f))
            if source.is_file():
                destination=target/name/str(f).replace('..','parent');destination.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(source,destination)
    records.append({'name':name,'version':version,'project':distribution.metadata.get('Home-page','')})
for name,url in {
    'OCCT-LGPL-2.1.txt':'https://raw.githubusercontent.com/Open-Cascade-SAS/OCCT/V7_8_1/LICENSE_LGPL_21.txt',
    'OCCT-LGPL-EXCEPTION.txt':'https://raw.githubusercontent.com/Open-Cascade-SAS/OCCT/V7_8_1/OCCT_LGPL_EXCEPTION.txt',
    'OCP-LICENSE.txt':'https://raw.githubusercontent.com/CadQuery/OCP/master/LICENSE'
}.items():
    with urllib.request.urlopen(url,timeout=30) as response:(target/name).write_bytes(response.read())
python_license=Path(sys.base_prefix)/'LICENSE.txt'
if python_license.exists():shutil.copyfile(python_license,target/'PYTHON-LICENSE.txt')
(target/'DEPENDENCIES.json').write_text(json.dumps(records,indent=2,ensure_ascii=False),encoding='utf8')
(target/'SOURCES.txt').write_text('OCCT native engine: https://github.com/Open-Cascade-SAS/OCCT/tree/V7_8_1\nOCP bindings: https://github.com/CadQuery/OCP\nOCP wheel recipe: https://github.com/CadQuery/ocp-build-system\nPDFium/PDF renderer: https://github.com/pypdfium2-team/pypdfium2\nRapidOCR: https://github.com/RapidAI/RapidOCR\nONNX Runtime: https://github.com/microsoft/onnxruntime\n',encoding='utf8')
print('Dependency notices:',len(records))
