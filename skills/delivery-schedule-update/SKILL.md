---
name: delivery-schedule-update
description: "Updates an attached Excel delivery schedule from the information in the mail: changed dates and quantities, status column, highlights of changed rows. The attachment is not modified; a new copy is saved after preview."
mailstudio:
  title: "Update delivery schedule (Excel)"
  title_tr: "Teslim planını güncelle (Excel)"
  inputs: [mail, xlsx]
  run: [button]
  output: excel
---

# Update delivery schedule

The mail contains changes to orders (new dates, quantities, statuses, cancellations) and the attached Excel file
is the delivery schedule.

1. Find each part number mentioned in the mail in the sheet.
2. Change only the cells that the mail asks to change (due date, quantity, status). Keep formulas: if a total
   is a formula, do not overwrite it.
3. Give every changed row a light yellow fill (#FFF2CC) and add a comment on the changed cell with the reason
   and the mail date.
4. If the mail mentions a part number that is not in the sheet, do not invent a row – mention it in the summary.

Dates in ISO format (YYYY-MM-DD) unless the sheet uses another format.
