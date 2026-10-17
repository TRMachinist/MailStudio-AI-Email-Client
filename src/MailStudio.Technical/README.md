# Technical attachment worker

Python 3.12 x64 with OCCT (OCP), PDFium, RapidOCR/ONNX and ezdxf pinned in `requirements.lock.txt`. The WPF app starts
one stdio JSON process per viewer; when the viewer closes the process and its retained geometry/model memory are released.
No local HTTP server is opened.

Build: see [docs/BUILDING.md](../../docs/BUILDING.md).

- The OCCT B-Rep is the measurement source; display triangulation is never used for measurements.
- `step_open` returns native edge/vertex IDs; `inspect` gives normal/axis/line direction, area, length and analytic diameters.
- `measure` uses native extrema/projection for face/edge/vertex/face-point mixes; minimum and picked-point vectors are separate.
- `envelope` takes six box face allowances, radial/end cylinder allowances and a global stock offset. The radial bound of the
  cylinder is computed from rotated native B-Rep support planes with a sec(π/N) upper bound — never from the screen mesh.
- AP242 semantic PMI and the XCAF graphical presentation geometry of the file are read; no drawing is synthesized for
  semantic PMI without a presentation. OCR is uncertain and its `review` flag is never cleared.
- `cad2d_*`: DXF via ezdxf, DWG via ODA File Converter (if installed) or the bundled LibreDWG `dwg2dxf`.
- `pdfs_*`: PDF studio (render with supersampling, words, objects, OCR, search, saving edits to a copy).

User-facing messages are written in Turkish; the WPF app translates them for the English UI.
