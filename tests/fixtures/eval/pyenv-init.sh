# Synthetic pyenv init - output fixture
# Mimics what `pyenv init - bash` would emit

export PYENV_ROOT="$HOME/.pyenv"
export PATH="$PYENV_ROOT/bin:$PATH"

eval "$(pyenv init -)"
