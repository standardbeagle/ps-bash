# Synthetic nvm function fixture
# Trimmed down nvm() function for testing Invoke-BashEval
# Just enough for `nvm use` to be callable

nvm() {
  if [ "$1" = "use" ]; then
    export NVM_CURRENT_VERSION="$2"
    export PATH="$HOME/.nvm/versions/node/v$2/bin:$PATH"
  elif [ "$1" = "deactivate" ]; then
    unset NVM_CURRENT_VERSION
  fi
}
