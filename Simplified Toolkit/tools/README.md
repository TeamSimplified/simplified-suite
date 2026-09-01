# tools/

Drop **`texconv.exe`** here for `erp textures … --png` to work.

Source: <https://github.com/microsoft/DirectXTex/releases> (download `texconv.exe`, MIT licensed).

If the file is missing the CLI will fall back to:
1. `TEXCONV` environment variable
2. `texconv` on PATH

If none resolve, PNG conversion commands error out clearly.
