---
name: drawing-check
description: "Checks a technical drawing (PDF or DXF/DWG) for manufacturability and completeness: title block, general tolerance, missing dimensions, tolerances, surface finish and notes."
mailstudio:
  title: "Technical drawing check"
  title_tr: "Teknik resim kontrolü"
  inputs: [pdf, dxf]
  run: [button]
  output: note
---

# Technical drawing check

Review the attached drawing(s) like an experienced manufacturing engineer.

Report, in this order:

1. **Title block** – part number, revision, material, scale, general tolerance standard (e.g. ISO 2768-mK),
   surface finish default, units. Mark anything that is missing.
2. **Toleranced features** – list every dimension with a tolerance or fit, and every geometric tolerance
   (with datum references). Flag tolerances that are very tight for the size.
3. **Surface roughness** and special notes (heat treatment, coating, deburring, marking).
4. **Possible problems** – dimensions that do not add up, missing datums, unclear notes, OCR uncertainty.
5. **Questions** to clarify with the customer.

Quote values exactly as written on the drawing. If the text came from OCR, say that values must be verified.
