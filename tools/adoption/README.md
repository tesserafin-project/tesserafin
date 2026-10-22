# Adoption dry run

See [the operator and evidence guide](../../docs/adoption/jellyfin-dry-run.md).

- `jellyfin_dry_run.py capture`: bounded GET-only capture from the selected source/target.
- `jellyfin_dry_run.py plan`: offline simulation with explicit account/path mappings.
- `jellyfin_state_apply.py`: experimental reviewed existing-account state application,
  journalled restart and conditional rollback; see [its separate safety/limits guide](../../docs/adoption/jellyfin-state-apply.md).
- No credentials transfer, account creation or media-file operation exists.
- `fixtures/` contains synthetic examples only; put real snapshots/reports elsewhere.

```sh
python3 -m unittest discover -s tools/adoption -p 'test_*.py' -v
```
