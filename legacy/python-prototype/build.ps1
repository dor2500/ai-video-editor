# Builds dist\CapCutAIEditor.exe
python -m pip install -r requirements.txt
python -m PyInstaller --noconfirm --clean --onefile --windowed --name CapCutAIEditor `
  --add-data "ui.html;." `
  --collect-all pycapcut --collect-all pymediainfo --collect-submodules webview `
  app.py
