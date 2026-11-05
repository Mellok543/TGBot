# Reklio deployment via GitHub Actions

The workflow `.github/workflows/reklio-deploy.yml` is **manual-only** until the first deployment has been tested. It deploys the existing `feature/miniapp-typescript-foundation` branch to the current VPS. The Telegram bot in Pterodactyl is **not** redeployed by this workflow.

## One-time server preparation (run as root on VPS)

1. Merge the deployment PR into `feature/miniapp-typescript-foundation` and update the checkout on VPS.
2. Install the root-owned deployment script:
   ```bash
   install -o root -g root -m 0755 /opt/reklio/source/deploy/reklio-deploy.sh /usr/local/sbin/reklio-deploy
   useradd --create-home --shell /bin/bash reklio-deploy
   install -d -o reklio-deploy -g reklio-deploy -m 0700 /home/reklio-deploy/.ssh
   touch /home/reklio-deploy/.ssh/authorized_keys
   chown reklio-deploy:reklio-deploy /home/reklio-deploy/.ssh/authorized_keys
   chmod 0600 /home/reklio-deploy/.ssh/authorized_keys
   ```
   If the user already exists, skip `useradd`.
3. On your own trusted machine, create a **dedicated key**:
   ```bash
   ssh-keygen -t ed25519 -f ~/.ssh/reklio_github_deploy -C "reklio-actions" -N ""
   ```
   Append the **public** `.pub` key to `/home/reklio-deploy/.ssh/authorized_keys` on VPS. Do not send the private key in chat.
4. Create the root-owned sudoers rule:
   ```bash
   printf '%s\n' 'reklio-deploy ALL=(root) NOPASSWD: /usr/local/sbin/reklio-deploy' > /etc/sudoers.d/reklio-deploy
   chmod 0440 /etc/sudoers.d/reklio-deploy
   visudo -cf /etc/sudoers.d/reklio-deploy
   ```
5. On the VPS, verify the actual SSH server public host key and fingerprint:
   ```bash
   cat /etc/ssh/ssh_host_ed25519_key.pub
   ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
   ```
   Build a known_hosts line using your **actual VPS IP or DNS**, a space, and the contents of the host public key (starting `ssh-ed25519`). Do not blindly trust unverified `ssh-keyscan` output.

## GitHub configuration

Under repository Settings → Secrets and variables → Actions, add:
- `REKLIO_DEPLOY_HOST`: your VPS public IP or DNS.
- `REKLIO_DEPLOY_USER`: `reklio-deploy`.
- `REKLIO_DEPLOY_PORT`: SSH port (usually `22`).
- `REKLIO_DEPLOY_SSH_KEY`: entire contents of the **private** dedicated deployment key, including BEGIN/END lines.
- `REKLIO_SSH_KNOWN_HOSTS`: verified known_hosts entry for the VPS host/port.

Create a GitHub Actions environment named `production` and, optionally, require manual approval for it. Repository-level secrets are used here, but environment-specific secrets can be used instead.

Run Actions → Deploy Reklio to VPS → Run workflow, selecting `feature/miniapp-typescript-foundation`.

## Safety notes

- The deployment script aborts if the VPS checkout has uncommitted changes; stash or commit those first.
- Builds happen before the API is stopped. A copy of the previous API is retained in `/opt/reklio/api-previous`.
- The script must remain **root-owned** and must not be writable by `reklio-deploy`.
- Do not put the SSH private key, bot token, database URL, or YooKassa secret in the repository.
- The workflow does not modify the database schema and does not deploy the Pterodactyl bot.
- After the manual test passes, automatic deployment on merges can be enabled deliberately.
