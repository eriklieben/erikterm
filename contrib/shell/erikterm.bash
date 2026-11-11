# erikterm shell integration for bash (4.4+, and 3.2 as macOS ships it) — works with starship, bash-preexec and
# plain prompts.
#
#   OSC 133 semantic prompts: A prompt start · B command start · C;cmdline_url=… output start (with the
#   command line, as erikterm.zsh sends it) · D;status end.  OSC 7: the working directory.
#
# Source it from ~/.bashrc *after* your prompt is set up (e.g. after `eval "$(starship init bash)"`):
#
#   [[ $TERM_PROGRAM == erikterm ]] && source /path/to/erikterm/contrib/shell/erikterm.bash
#
# How: PROMPT_COMMAND gets a hook in front (D + OSC 7, while $? is still the command's) and one at the end
# (wraps PS1 in A … B, after starship has rebuilt it). The C mark comes from PS0, which bash prints after reading
# a command line and before running it; the command line is taken from history there, and only when the
# command was added to history (so ignorespace / ignoredups / HISTIGNORE / `set +o history` send a plain C).
# No DEBUG trap is used, so bash-preexec (and starship or atuin on top of it) keep working. bash before 4.4 (macOS's
# /bin/bash is 3.2) has no PS0: there the C mark comes from bash-preexec's preexec_functions when it is loaded, else
# from a DEBUG trap — only when no other DEBUG trap is set — that sends it once, for the first command after a prompt.
#
# $? and PIPESTATUS: after our first hook, a pipeline of the same length re-creates both, so hooks after it
# (starship's --status / --pipestatus) see the command's values.

[[ $- == *i* ]] || return 0
[[ -n $TMUX && $TMUX != "$ERIKTERM_SOCKET",* ]] && return 0  # real tmux (not erikterm's emulation)
[[ -n $__erikterm_integration ]] && return 0
__erikterm_integration=1
__erikterm_hist=-1
__erikterm_ps=(0)

# Percent-encodes $1 (bytewise, UTF-8) into REPLY.
__erikterm_urlencode() {
  local LC_ALL=C s=$1 enc="" c i
  if [[ $s != *[^A-Za-z0-9/._~-]* ]]; then REPLY=$s; return; fi
  for (( i = 0; i < ${#s}; i++ )); do
    c=${s:i:1}
    case $c in
      [A-Za-z0-9/._~-]) enc+=$c ;;
      *) printf -v c '%%%02X' "'$c"; enc+=${c: -3} ;; # bytes >= 0x80 can come out sign-extended
    esac
  done
  REPLY=$enc
}

__erikterm_osc7() {
  local REPLY
  __erikterm_urlencode "$PWD"
  printf '\e]7;file://%s%s\e\\' "${HOSTNAME}" "${REPLY}"
}

# The marks as they appear in PS1 (\[ \] so bash doesn't count them in the prompt's width).
__erikterm_A=$'\\[\e]133;A\e\\\\\\]' __erikterm_B=$'\\[\e]133;B\e\\\\\\]'

# oh-my-posh with shell_integration marks prompts and commands itself, and so does a prompt with OSC 133 of
# its own in PS1 (as an escape or spelled out for bash; ours are stripped first).
__erikterm_prompt_marks_by_framework() {
  [[ ${_omp_ftcs_marks:-0} == 1 ]] && return 0
  local p=${PS1//"$__erikterm_A"/}
  p=${p//"$__erikterm_B"/}
  [[ $p == *$'\e]133;'* || $p == *'\e]133;'* || $p == *'\033]133;'* ]]
}

# First in PROMPT_COMMAND: report the status while $? is still the command's (erikterm ignores a D that
# wasn't preceded by C, i.e. when no command ran), remember PIPESTATUS for the restore below, and note
# the history number so PS0 can tell whether the next command line went into history.
__erikterm_prompt_first() {
  local ret=$? ps=("${PIPESTATUS[@]}")
  __erikterm_ps=("${ps[@]}")
  __erikterm_prompt_marks_by_framework || printf '\e]133;D;%d\e\\' "$ret"
  __erikterm_osc7
  local h
  h=$(HISTTIMEFORMAT='' builtin history 1)
  [[ $h =~ ^[[:space:]]*([0-9]+) ]] && __erikterm_hist=${BASH_REMATCH[1]} || __erikterm_hist=-1
  return "$ret"
}

__erikterm_status() { return "$1"; }

# Run right after __erikterm_prompt_first, as a command of its own: a pipeline as long as the last one
# leaves $? and PIPESTATUS as the command left them (the last status only beyond 8 stages).
__erikterm_restore='case ${#__erikterm_ps[@]} in
  2) __erikterm_status "${__erikterm_ps[0]}" | __erikterm_status "${__erikterm_ps[1]}" ;;
  3) __erikterm_status "${__erikterm_ps[0]}" | __erikterm_status "${__erikterm_ps[1]}" | __erikterm_status "${__erikterm_ps[2]}" ;;
  4) __erikterm_status "${__erikterm_ps[0]}" | __erikterm_status "${__erikterm_ps[1]}" | __erikterm_status "${__erikterm_ps[2]}" | __erikterm_status "${__erikterm_ps[3]}" ;;
  5) __erikterm_status "${__erikterm_ps[0]}" | __erikterm_status "${__erikterm_ps[1]}" | __erikterm_status "${__erikterm_ps[2]}" | __erikterm_status "${__erikterm_ps[3]}" | __erikterm_status "${__erikterm_ps[4]}" ;;
  6) __erikterm_status "${__erikterm_ps[0]}" | __erikterm_status "${__erikterm_ps[1]}" | __erikterm_status "${__erikterm_ps[2]}" | __erikterm_status "${__erikterm_ps[3]}" | __erikterm_status "${__erikterm_ps[4]}" | __erikterm_status "${__erikterm_ps[5]}" ;;
  7) __erikterm_status "${__erikterm_ps[0]}" | __erikterm_status "${__erikterm_ps[1]}" | __erikterm_status "${__erikterm_ps[2]}" | __erikterm_status "${__erikterm_ps[3]}" | __erikterm_status "${__erikterm_ps[4]}" | __erikterm_status "${__erikterm_ps[5]}" | __erikterm_status "${__erikterm_ps[6]}" ;;
  8) __erikterm_status "${__erikterm_ps[0]}" | __erikterm_status "${__erikterm_ps[1]}" | __erikterm_status "${__erikterm_ps[2]}" | __erikterm_status "${__erikterm_ps[3]}" | __erikterm_status "${__erikterm_ps[4]}" | __erikterm_status "${__erikterm_ps[5]}" | __erikterm_status "${__erikterm_ps[6]}" | __erikterm_status "${__erikterm_ps[7]}" ;;
  *) __erikterm_status "${__erikterm_ps[${#__erikterm_ps[@]}-1]}" ;;
esac'

# Last in PROMPT_COMMAND: starship rebuilds PS1 in its own hook, so mark it afterwards.
__erikterm_prompt_last() {
  local ret=$?
  if ! __erikterm_prompt_marks_by_framework; then
    [[ $PS1 == "$__erikterm_A"* ]] || PS1="$__erikterm_A$PS1"
    [[ $PS1 == *"$__erikterm_B" ]] || PS1="$PS1$__erikterm_B"
  fi
  __erikterm_at_prompt=1
  return $ret
}

# Expanded in PS0 (in a subshell, after the command line went into history): output starts here.
__erikterm_ps0() {
  __erikterm_prompt_marks_by_framework && return 0
  local h REPLY
  h=$(HISTTIMEFORMAT='' builtin history 1)
  if [[ $h =~ ^[[:space:]]*([0-9]+)\*?[[:space:]]+ ]] && (( BASH_REMATCH[1] != __erikterm_hist )); then
    h=${h#"${BASH_REMATCH[0]}"}
    __erikterm_urlencode "${h:0:2048}"
    printf '\e]133;C;cmdline_url=%s\e\\' "$REPLY"
  else
    printf '\e]133;C\e\\'
  fi
}

__erikterm_at_prompt=0

# bash < 4.4: the C mark before the first command after a prompt (the DEBUG trap also runs for PROMPT_COMMAND's
# commands, which come before __erikterm_prompt_last sets the flag).
__erikterm_preexec_legacy() {
  (( __erikterm_at_prompt )) || return 0
  __erikterm_at_prompt=0
  __erikterm_ps0
}

if (( BASH_VERSINFO[0] > 4 || (BASH_VERSINFO[0] == 4 && BASH_VERSINFO[1] >= 4) )); then
  # PS0 needs promptvars for the command substitution; without it, a plain C mark.
  if [[ $PS0 != *__erikterm_ps0* && $PS0 != *$'\e]133;C'* ]]; then
    if shopt -q promptvars; then PS0+='$(__erikterm_ps0)'; else PS0+=$'\e]133;C\e\\'; fi
  fi
elif [[ "$(declare -p preexec_functions 2>/dev/null)" == "declare -a"* ]]; then
  preexec_functions+=(__erikterm_preexec_legacy)
elif [[ -z "$(trap -p DEBUG)" ]]; then
  trap '__erikterm_preexec_legacy' DEBUG
fi

if [[ "$(declare -p PROMPT_COMMAND 2>/dev/null)" == "declare -a"* ]]; then
  PROMPT_COMMAND=(__erikterm_prompt_first "$__erikterm_restore" "${PROMPT_COMMAND[@]}" __erikterm_prompt_last)
else
  PROMPT_COMMAND="__erikterm_prompt_first; ${__erikterm_restore}${PROMPT_COMMAND:+; $PROMPT_COMMAND}; __erikterm_prompt_last"
fi
