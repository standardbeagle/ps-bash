#!/usr/bin/env ps-bash
# ---------------------------------------------------------------------------
# z / zi — zoxide-style smart cd (INTERACTIVE SHELL ONLY).
# z/zi are a prompt-side rewrite, not a cmdlet, so `ps-bash -c "z foo"` is a
# documented v1 gap. This file is a walkthrough to run by hand inside `ps-bash`.
# ---------------------------------------------------------------------------

# 1. Seed the frecency database by visiting a few directories (any cd counts —
#    cd, pushd, scripts, even z itself):
cd ../../src
cd ../docs
cd ..

# 2. Now jump by keyword. Highest frecency wins; the LAST keyword must hit the
#    final path component:
#       z src           -> .../src
#       z docs          -> .../docs
#       z core ps       -> .../core/ps-bash   (if visited)
#
# 3. Pick from a list when several match:
#       zi src          -> numbered menu, type the number to cd
#
# 4. No args -> home:
#       z               -> cd ~
#
# Tab completion and ghost text for cd / z / zi come from the same frecency DB
# (~/.psbash/frecency.db). Try:  z <Tab>   and   cd <space>  (ghost preview).
echo "Open 'ps-bash' and try: z src   |   zi docs   |   z <Tab>"
