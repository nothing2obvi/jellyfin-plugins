# Artist Pruner development status

**IN DEVELOPMENT — DO NOT USE.**

Artist Pruner is unfinished. Do not install or run it against a Jellyfin library,
including in dry-run mode. Published ZIP files and catalog entries are development
artifacts; their availability does not mean they are safe, supported, or ready for use.

## Why this warning exists

- A live-server run exposed an API compatibility failure despite passing automated tests.
- Pruning, replacement, metadata locking, and recovery behavior remain under development.
- Automated tests do not establish safety or correct behavior on real Jellyfin libraries.
- Backup and restore features are not a guarantee against metadata or user-state problems.
- Some changes are unpublished; descriptions of development work are not release guarantees.

## If already installed

Disable Artist Pruner in its plugin settings. Remove any scheduled triggers for
**Prune Music Artists** and turn off automatic runs after library scans. Do not
start pruning, replacement, or restore operations while this warning is in effect.

Preserve independent server backups, plugin recovery files, and relevant logs.
If full mode has already run, do not delete recovery files or manually change
plugin-managed metadata locks without a reviewed recovery plan.

The remaining README documents implementation and developer build references only.
It is not a recommendation to use the plugin. This warning applies to all currently
published Artist Pruner versions until explicitly withdrawn in the documentation.
