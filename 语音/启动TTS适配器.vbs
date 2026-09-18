Dim fso, root, adapter
Set fso = CreateObject("Scripting.FileSystemObject")
root = fso.GetParentFolderName(fso.GetParentFolderName(WScript.ScriptFullName))
adapter = fso.GetParentFolderName(WScript.ScriptFullName) & "\openai_tts_adapter.py"
' 相对路径启动 TTS 适配器（不依赖安装位置）；venv 名默认为 sovits-venv
CreateObject("Wscript.Shell").Run """" & root & "\sovits-venv\Scripts\pythonw.exe"" """ & adapter & """", 0, False
