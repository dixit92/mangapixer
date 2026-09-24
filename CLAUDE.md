# MangaPixer - Claude Code project instructions

Project instructions for all contributors and agents live in [AGENTS.md](AGENTS.md); this file only imports them for Claude Code.

@AGENTS.md

## Docker testing environment

Build and test in containers as described in the container-based build sections of AGENTS.md; `deploy/compose.yaml` (plus `deploy/compose.dev.yaml` where needed) is the reference container layout. If the Docker daemon is remote or shared, bind mounts will not see your working tree: stream it in (`tar ... | docker cp`, or a build context) instead. Name every container and image you create with a unique prefix and remove only your own.
