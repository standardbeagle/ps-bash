# Combined assignments fixture for Invoke-BashEval integration tests
# Mix of export, bare assignment, and env-prefix forms

# Standard exports
export VAR1=value1
export VAR2="quoted value"

# Bare assignments (not exported)
LOCAL_VAR=local_value
ANOTHER_LOCAL=42

# Env-prefix form (command-scoped)
TEMP_VAR=temp_value echo "setting temp"

# Export with command
export PATH=/usr/bin:/bin

# Multiple exports on one line
export A=1 B=2 C=3

# Unset
unset LOCAL_VAR
