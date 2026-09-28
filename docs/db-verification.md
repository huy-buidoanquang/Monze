# DB verification for live command checks

PostgreSQL is the source of truth for Monze business state. The inspection script reads only selected aggregate counts and status fields; it never prints the connection string or credentials.

Run it from the Monze directory after a live test:

```powershell
& .\scripts\inspect-db.ps1 -ClanId <clan> -ChannelId <channel> -Assert |
  ConvertTo-Json -Depth 6 |
  Tee-Object -FilePath logs\db-inspection-final.json
```

After a smoke test, remove only the named test data with:

```powershell
& .\scripts\cleanup-live-test.ps1 -ClanId <clan> -ChannelId <channel> -UserId <test-user>
```

The checks cross-reference clan registration, welcome settings, role rules, points ledger and balance, meeting schedule kinds, meeting session status, FAQ count, topic prompts and wheel cooldowns. `-Assert` also requires an active clan, migrations 007 through 011, a settings row, at least one topic prompt, and equal ledger and balance totals. A successful UI response is not treated as proof until its corresponding row or aggregate is visible in this report.
