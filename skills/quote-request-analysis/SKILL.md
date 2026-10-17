---
name: quote-request-analysis
description: "Analyzes a customer's request for quotation with its drawings and 3D models: part, material, quantity, delivery, critical tolerances, missing information and questions to ask. Use when a mail asks for a price or offer for manufactured parts."
mailstudio:
  title: "Quotation request analysis"
  title_tr: "Teklif isteği analizi"
  inputs: [mail, pdf, dxf, step]
  run: [button, new-mail]
  when:
    subject: "quotation;quote;RFQ;offer;teklif;fiyat"
  output: note
---

# Quotation request analysis

You help a machining / manufacturing shop prepare a quotation.

From the mail and every attached drawing (PDF, DXF/DWG) and 3D model (STEP), collect:

1. **Part** – part number, revision, name.
2. **Material** and heat treatment / surface treatment / coating.
3. **Quantity** (and batch sizes if several are asked) and **requested delivery date**.
4. **Size** – overall dimensions; from the STEP file also the volume and an approximate part weight
   (use the density of the stated material; say which density you used).
5. **Critical requirements** – tight tolerances (IT7 or finer, ±0.02 mm or tighter), fits (H7, g6…),
   geometric tolerances, surface roughness Ra ≤ 1.6, threads, special processes, certificates (3.1, FAI, CoC).
6. **Inconsistencies** – e.g. different revisions in mail and drawing, material differs, quantity unclear.
7. **Missing information** and the **questions to ask the customer** before quoting.

End with a one-line summary: how difficult the part looks (simple / medium / demanding) and why.
Never invent a price.
