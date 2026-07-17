# Email Alert Troubleshooting

Email alerts are optional. Live calls can trigger email notifications when alert
rules match and the active profile allows the call. Historical/imported calls
are not currently evaluated by the alert pipeline.

## Required Settings

In **Settings -> Alerts** configure:

- email provider;
- sender account and app password;
- recipient on each alert rule;
- keyword, police-code, or combined criteria;
- optional system-scoped talkgroups.

Use the **Test** action before enabling alerts.

## Gmail Notes

For Gmail, use an app password rather than your account password. The account
must have two-factor authentication enabled before app passwords are available.

Typical settings:

| Field | Value |
| --- | --- |
| Host | `smtp.gmail.com` |
| Port | `587` |
| TLS | enabled |

## Troubleshooting

Check:

```bash
journalctl -u pizzad -n 100 --no-pager
```

Common failures:

- wrong SMTP port or TLS mode;
- account blocks app password use;
- recipient rejected by provider;
- network firewall blocks outbound SMTP;
- the active profile excludes the call category or talkgroup;
- a talkgroup rule points at a different system with the same numeric TG ID.
