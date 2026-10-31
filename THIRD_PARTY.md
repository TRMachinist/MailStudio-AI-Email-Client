# Third-party components

MailStudio itself is MIT licensed. It uses or bundles the following components, each under its own license.
Full license texts are shipped in the `ThirdPartyNotices` folder of the release package.

## .NET libraries (NuGet)

| Component | License |
|---|---|
| MailKit / MimeKit | MIT |
| AvalonEdit | MIT |
| ClosedXML | MIT |
| Open XML SDK | MIT |
| HtmlToOpenXml | MIT |
| HtmlSanitizer | MIT |
| CommunityToolkit.Mvvm | MIT |
| DnsClient | Apache-2.0 |
| Google.Apis.Auth | Apache-2.0 |
| Microsoft.Identity.Client (MSAL) | MIT |
| Microsoft.Data.Sqlite, Microsoft.Extensions.Hosting | MIT |
| Microsoft Edge WebView2 SDK | BSD-3-Clause style (Microsoft) |
| ModelContextProtocol C# SDK | Apache-2.0 / MIT |
| SharpCompress | MIT |

### SharpCompress License (MIT)
Copyright (c) 2014-2025 Adam Hathcock
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Technical worker (Python, frozen with PyInstaller)

| Component | License |
|---|---|
| Open CASCADE Technology via cadquery-ocp | LGPL-2.1 with OCCT exception |
| ezdxf | MIT |
| pypdfium2 / PDFium | Apache-2.0 / BSD-3-Clause |
| RapidOCR, ONNX Runtime | Apache-2.0 / MIT |
| OpenCV (headless) | Apache-2.0 |
| NumPy, Shapely, Pillow, fontTools, pyclipper and others | BSD / MIT / HPND (see notices) |
| PyInstaller bootloader | GPL-2.0 with bootloader exception |

## Bundled separate programs

These are shipped unmodified as **separate executables** and started as external processes; MailStudio does not link
to them.

| Program | License | Source |
|---|---|---|
| LibreOffice (trimmed portable) | MPL-2.0 (parts LGPL / Apache) | https://www.libreoffice.org/about-us/source-code/ |
| LibreDWG `dwg2dxf` | GPL-3.0-or-later | https://github.com/LibreDWG/libredwg |

Not bundled but used if installed: Microsoft Office, ODA File Converter, Ollama, Claude Code, Codex CLI, Gemini CLI.

Binaries of these components are **not** stored in this repository; they are added to the release package by the
scripts in `tools/packaging`.
