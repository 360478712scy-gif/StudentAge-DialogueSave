"""Change only the cloned player's native preference namespace; preserve raw field sizes."""
from pathlib import Path
import struct
import UnityPy
root=Path(__file__).resolve().parents[1]
path=root/'qa/runtime/StudentAge_Data/data.unity3d'
env=UnityPy.load(str(path))
settings=[o for o in env.objects if o.type.name=='PlayerSettings']
assert len(settings)==1
obj=settings[0];raw=obj.get_raw_data()
for old,new in [(b'PakyiGame',b'DlgSaveQA'),(b'StudentAge',b'DialogSave')]:
    assert len(old)==len(new)
    if new in raw:continue
    marker=struct.pack('<i',len(old))+old
    assert raw.count(marker)==1
    raw=raw.replace(marker,struct.pack('<i',len(new))+new)
obj.set_raw_data(raw)
path.write_bytes(env.file.save(packer='lz4'))
(path.parent/'app.info').write_text('DlgSaveQA\nDialogSave')
assert b'PakyiGame' not in next(o for o in UnityPy.load(str(path)).objects if o.type.name=='PlayerSettings').get_raw_data()
print('QA_NATIVE_IDENTITY_ISOLATED')
