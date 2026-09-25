# Remote MCP access through Cloudflare

Cloudflare Tunnel can expose the container MCP server without publishing a host port. Put a Cloudflare Access self-hosted application with Managed OAuth in front of the tunnel hostname. The [security model](security.md) describes the trust boundary; [Docker setup](getting-started-docker.md) covers the stack itself.

## Configure the tunnel and Access

1. Create a remotely managed tunnel whose origin is `http://mcp:3333`. Keep the MCP container's host port unpublished.
2. Protect the public hostname with a self-hosted Access application. Enable Managed OAuth and allow `https://claude.ai/api/mcp/auth_callback` as a redirect URI for Claude connectors. Limit the application policy to the identities that should read the archive.
3. Set `TUNNEL_TOKEN` and `MCP_PUBLIC_HOSTNAME` in `.env`, then start the sidecar with `docker compose --profile tunnel up -d`. The public hostname must be in `Mcp:AllowedHosts`; compose maps `MCP_PUBLIC_HOSTNAME` to that setting.
4. Add the HTTPS hostname as a custom MCP connector in the client. The MCP endpoint is `/`.

Claude's hosted connector reaches the public endpoint from Anthropic's infrastructure. Ensure your Access and network rules permit that traffic; check the client's current published requirements before adding IP restrictions.

The tunnel's ingress should forward `/` to `mcp:3333` and return 404 for `/health`. Use the exact path expression `^/health$`; tunnel path patterns are regular expressions. The origin also restricts `/health` to loopback by default. External monitors use `/up`.

## Scope a monitoring credential

Create a separate Access application for the exact `/up` path and authorize only a monitoring service token there. Do not grant that token access to the root application. In particular, a root policy using **Any Access Service Token** grants every service token access to the MCP tools. Check the actual policy rule in the Cloudflare dashboard; its name is not evidence of its scope.

From outside the host, test the monitoring token against both paths: `/up` must return status JSON; a `tools/list` request to `/` must be denied. The [monitoring guide](monitoring-uptime-kuma.md) includes a probe. `/health` must not return its detailed body. Access applications and ingress rules live outside this repository, so repeat these checks after changing them.

## Origin validation of the Access assertion (`Mcp:Access`)

`Mcp:Access` lets the MCP server validate `Cf-Access-Jwt-Assertion` itself. It is off by default and recommended when using a tunnel. Supply these values in `.env`:

| Key | Value |
| --- | --- |
| `MCP_ACCESS_ENABLED` | `true` |
| `MCP_ACCESS_TEAM_DOMAIN` | Full `https://<team>.cloudflareaccess.com` URL |
| `MCP_ACCESS_AUDIENCE` | AUD tag of the root Access application |
| `MCP_ACCESS_MONITORING_AUDIENCE` | AUD tag of the separate `/up` application, if used |
| `MCP_ACCESS_ALLOWED_IDENTITIES` | Comma-separated owner emails and permitted service-token client IDs; exclude the monitoring token |

Get AUD tags from each application's Additional settings in Zero Trust. Recreate the container with `docker compose up -d mcp`; `docker compose restart` retains the old environment. The server refuses an incomplete configuration. Keep `MCP_ACCESS_ALLOW_LOOPBACK=true` so the container healthcheck and `mailvec doctor` can use loopback `/health` without a token.

The identity allowlist matters even with separate audiences: if the root Access policy admits a monitoring token, Cloudflare can issue it a root-application assertion. An allowlist at the origin can still refuse that identity.

## Verify

Use your own hostname in these commands. For the last probe, set `CF_ID` and `CF_SECRET` to the monitoring token's credentials in your shell.

```sh
# Detailed health is available only inside the MCP container.
docker compose exec mcp curl -fsS http://127.0.0.1:3333/health

# The public minimal endpoint answers after owner authentication.
curl -i https://mailvec.example.com/up

# The public detailed endpoint must not return its body.
curl -i https://mailvec.example.com/health

# Monitoring credential can read /up.
curl -i -H "CF-Access-Client-Id: $CF_ID" \
  -H "CF-Access-Client-Secret: $CF_SECRET" \
  https://mailvec.example.com/up

# The same credential must not list MCP tools.
curl -i -X POST -H "CF-Access-Client-Id: $CF_ID" \
  -H "CF-Access-Client-Secret: $CF_SECRET" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' \
  https://mailvec.example.com/
```

A tool list in the last response means the monitoring token can read the archive. Correct the root Access policy and the origin identity allowlist. If the first public request returns a login page, authenticate as an allowed identity before interpreting the result. For connector discovery or OAuth failures, check the redirect URI, Access policy, and the client's current OAuth requirements.
