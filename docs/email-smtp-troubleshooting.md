# Email Alert Troubleshooting

Email alerts are optional. Live calls can trigger email notifications when alert
rules match; imported/historical calls store matches but suppress live email.

## Required Settings

In **Settings -> Alerts** configure:

- SMTP host and port;
- username;
- password or app password;
- sender address;
- recipient address;
- TLS/SSL mode.

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
- imported calls are being tested instead of live calls.
