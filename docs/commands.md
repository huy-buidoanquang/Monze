# Monze commands

`Monze:Commands` controls the command prefix and optional `monze` root without
changing application code.

```json
{
  "Monze": {
    "Commands": {
      "Prefix": "*",
      "Root": "monze"
    }
  }
}
```

- `Prefix: "*"` produces `*monze welcome on` and `*welcome on`.
- `Prefix: ""` removes the leading `*`.
- `Root: "monze"` enables `*monze help` and rooted module commands. Every module
  command is also registered directly, for example `*meeting` and `*welcome on`.
- `Root: ""` selects rootless mode; general help becomes `*help`.

## Current modules

- Meeting: `meeting`, `meeting now`, `meeting cancel <id>`, and once/daily/weekly
  schedules. The interactive meeting card also opens the schedule form.
- Summary: `summary <id>` for owner/admin retrieval of a stored meeting summary.
- Welcome: `welcome on|off`, `welcome message <text>`, `welcome message remove`,
  `welcome setup`, `welcome preview`, and `welcome setup remove`.
- Role: `role on|off`, `role join <role>`, `role tenure <role>`, and
  `role join|tenure remove <role>`.
- AI: `ai summary`, `ai translate`, `ai composer`, and `ai simplify`.
- Avatar: `avatar`, `avatar <username|@user>`, and `avatar reply message`; aliases
  `ava` and `avt` are retained.
- Setup: `setup admin add <@user>` and `setup admin remove <@user>`; only the clan
  owner sees and may execute this module. Adding an admin requires the target to be
  present in the current Mezon clan roster.

Append `help` to a module command to render its permission-aware guide. General help
shows only module representatives and uses buttons to navigate the same private
message. Keep credentials in ignored local settings, environment variables, or an
approved secret store.
