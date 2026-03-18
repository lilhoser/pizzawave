# Email SMTP Troubleshooting

Troubleshooting alert/digest email delivery for Gmail and Yahoo app-password mode.

## Required Settings

- `emailProvider`: `gmail` or `yahoo`
- `emailUser`: full email address
- `emailPassword`: app password (not account password)

`pizzapi` Settings panel includes a **Test** button to validate delivery.

## SMTP Endpoints (current code)

| Provider | Host | Port | TLS |
|---|---|---|---|
| Gmail | `smtp.gmail.com` | `587` | STARTTLS |
| Yahoo | `smtp.mail.yahoo.com` | `587` | STARTTLS |

## Common Failure Patterns

| Symptom | Likely cause | Action |
|---|---|---|
| `Failure sending mail` | provider rejected auth | verify app password, not account password |
| `SMTP status=GeneralFailure` | network or DNS issue | verify outbound access to SMTP host:587 |
| `Authentication failed` | wrong provider selected | set `emailProvider` to match domain/provider |
| timeout | firewall or provider throttling | allow egress 587, retry later |

## Diagnostics

Current sender errors include:
- provider
- SMTP host/port
- user
- SMTP status code
- inner exception message

Use those fields first before changing code/config broadly.

## Security Notes

- App passwords are sensitive secrets; protect `settings.json`.
- Prefer dedicated mailbox for notifications where possible.
