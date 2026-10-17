---
name: urgent-mail-flag
description: "Flags mails that need action today: production stops, quality problems, delivery delays, customer escalations, deadlines within 48 hours."
mailstudio:
  title: "Flag urgent mails"
  title_tr: "Acil mailleri işaretle"
  inputs: [mail]
  run: [button, new-mail]
  output: organize
---

# Flag urgent mails

Decide if this mail needs action today.

Urgent (flag = true): machine or production stop, quality problem / nonconformance / customer complaint,
delivery delay or missing material, explicit deadline within 48 hours, escalation from a manager or customer.

Not urgent (flag = null, do not change anything): newsletters, advertisements, automatic notifications,
general information, thank-you mails.

Never move or mark mails as read with this skill: use only "flag". Give the reason in one short sentence.
