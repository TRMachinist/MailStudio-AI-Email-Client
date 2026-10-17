---
name: step-cost-estimate
description: "Rough manufacturing estimate from a STEP model: envelope, raw stock size with machining allowance, part and stock weight, material removal and a rough machining-time class."
mailstudio:
  title: "STEP raw stock & weight estimate"
  title_tr: "STEP hammadde ve ağırlık tahmini"
  inputs: [step, mail]
  run: [button]
  output: note
---

# STEP raw stock & weight estimate

Use the STEP summary (exact B-Rep sizes and volumes) and the material mentioned in the mail.
If no material is given, calculate for aluminium 7075 (2.81 g/cm³) and steel (7.85 g/cm³) and say so.

1. **Part envelope** (X × Y × Z mm) and **part weight** (volume × density).
2. **Raw stock**: add 3 mm per side for plate/block (round up to standard plate thicknesses) or 2 mm on the
   diameter for round bar if the part looks turned. Give stock size and **stock weight**.
3. **Material removal** – stock weight minus part weight, and the removal ratio in %.
4. **Machining class** – simple / medium / complex, based on cylindrical faces (holes, bores),
   number of faces and the removal ratio. Explain in one sentence.
5. Tolerances found in the PMI (if any) that affect the process.

Show the formulas and units so the numbers can be checked. This is an estimate, not a price.
