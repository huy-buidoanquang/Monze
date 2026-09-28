# Monze commands

`Monze:Commands` controls the command surface without changing application code.

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

- `Prefix: "*"` produces `*monze points`.
- `Prefix: ""` removes the command prefix.
- `Root: "monze"` produces rooted commands such as `*monze welcome on`.
- `Root: ""` registers module commands directly, such as `*welcome on` and `*points`.
- Role administration supports self-select and persisted automatic rules: `*monze role allow <role>`, `*monze role self <role>`, `*monze role rule join <role>`, `tenure <role> | <days>`, `points <role> | <points>`, `existing <role> | <required role>`, and `role rule remove <kind> <role>`.
- Meeting and summary keep their direct command names: `*meeting ...` and `*summary`.

The default root is `monze` when `Root` is absent. Set an explicit empty string to select rootless mode. Keep the configuration in an ignored local or secret file when it contains credentials.
