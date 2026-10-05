# SSH access to the PizzaWave radio Pi

`sdr1861` is the radio Pi, account `ocroot`, port 22, reached through the
existing Tailscale address `100.105.110.92`. It is separate from OT and from
the disconnected `sdrhero`. This change does not install Tailscale or create
another network route.

On Paxan and Ventax, `ssh sdr1861` and `ssh rpi-radio` select the owner's
managed White Oak personal key through the existing local Pageant agent.
KeePass 2 and KeeAgent must have loaded the key. No agent forwarding is used.
The shortcut lives in each user's `.ssh/config`; it is not a DNS record and
does not automatically appear on other computers.

For another client, configure the same destination and account using that
user's own authorized key and local agent. Do not copy a Windows Pageant pipe
name or another person's private key. An ordinary OpenSSH agent is also
supported; the deployment scripts do not require Pageant or KeePass itself.
Verify the server host key before trusting a new client connection.

The standard attended deployment command is:

```powershell
.\scripts\deploy_pizzad_tar.ps1 -HostName sdr1861 -Rid linux-arm64
```

Omitting `-SshKey` lets SSH and SCP use the existing client configuration.
The frontend helper accepts the same host choice. A different client can
still use an explicitly supplied identity through `-SshKey`; a public selector
works only when its corresponding private key is loaded in that client's
agent. Do not configure unattended jobs to depend on an unlocked human vault.
Any unattended job needs its own separately approved service identity.

The former `pizzapi_rpi_test_ed25519` identity is being retired under the
White Oak machine-by-machine key audit. Historical field notes may name it;
they are not current login instructions. The authoritative retirement evidence
is in `whiteoakMt/docs/infrastructure/backup-and-recovery.md`.
