# erikterm shell integration for zsh — works with starship, powerlevel10k, pure and plain prompts.
#
#   OSC 133 (semantic prompts): A prompt start · B command start · C output start · D;status end.
#     Enables ScrollToPrompt (Shift+Up/Down), Select/Copy Command Output, failed-command markers.
#   OSC 7: the working directory, so new splits open where you are.
#
# Source it from ~/.zshrc *after* your prompt is set up (e.g. after `eval "$(starship init zsh)"`):
#
#   [[ $TERM_PROGRAM == erikterm ]] && source /path/to/erikterm/contrib/shell/erikterm.zsh
#
# Inside real tmux the sequences wouldn't reach erikterm, so nothing is done there. erikterm's tmux
# emulation ($TMUX naming $ERIKTERM_SOCKET) isn't real tmux, so the script runs there.
#
# $? and $pipestatus: zsh restores both after every precmd hook, so the prompt (starship's
# --status/--pipestatus, oh-my-posh's) sees the last command's values whatever order the hooks run in.
#
# Transient prompts (oh-my-posh's transient_prompt) redraw the prompt shorter after Enter, which erases the
# row that had the 133;B mark; the command line is also sent with 133;C (cmdline_url=) so erikterm still
# knows it. oh-my-posh with `shell_integration = true` emits the 133 marks itself (in the transient prompt
# too), and then this script only reports the directory.

[[ -o interactive ]] || return 0
[[ -n $TMUX && $TMUX != "$ERIKTERM_SOCKET",* ]] && return 0  # real tmux (not erikterm's emulation)
(( ${+__erikterm_integration} )) && return 0
typeset -g __erikterm_integration=1
typeset -gi __erikterm_running=0

autoload -Uz add-zsh-hook

# Percent-encodes $1 (bytewise, UTF-8) into REPLY.
__erikterm_urlencode() {
  emulate -L zsh
  setopt extendedglob
  local LC_ALL=C
  REPLY=${1//(#m)[^A-Za-z0-9\/._~-]/%${(l:2::0:)$(( [##16] #MATCH ))}}
}

# OSC 7: file://host/path, percent-encoded.
__erikterm_osc7() {
  local REPLY
  __erikterm_urlencode "$PWD"
  printf '\e]7;file://%s%s\e\\' "${HOST}" "${REPLY}"
}

typeset -g __erikterm_A=$'%{\e]133;A\e\\%}' __erikterm_B=$'%{\e]133;B\e\\%}'

# oh-my-posh with shell_integration = true marks prompts and commands itself, and so does a prompt with
# OSC 133 of its own in PS1 (ours are stripped first).
__erikterm_prompt_marks_by_framework() {
  [[ ${_omp_ftcs_marks:-0} == 1 ]] && return 0
  local p=${PS1//"$__erikterm_A"/}
  p=${p//"$__erikterm_B"/}
  [[ $p == *$'\e]133;'* ]]
}

# Marks the prompt's start (A) and the end, where typing begins (B). Idempotent.
__erikterm_wrap_prompt() {
  __erikterm_prompt_marks_by_framework && return
  [[ $PS1 == ${__erikterm_A}* ]] || PS1="${__erikterm_A}${PS1}"
  [[ $PS1 == *${__erikterm_B} ]] || PS1="${PS1}${__erikterm_B}"
}

# First precmd hook: report how the last command ended while $? is still its status, and hand
# the same status on to the hooks after us (starship reads it).
__erikterm_precmd_first() {
  local ret=$?
  if (( __erikterm_running )); then
    __erikterm_running=0
    __erikterm_prompt_marks_by_framework || printf '\e]133;D;%d\e\\' "$ret"
  fi
  __erikterm_osc7
  return $ret
}

# Last precmd hook: frameworks that rebuild PS1 in their own precmd (pure, p10k) have run by now.
__erikterm_precmd_last() {
  local ret=$?
  __erikterm_wrap_prompt
  return $ret
}

# Output starts; $1 is the command line as typed (sent so erikterm knows it even after a transient prompt).
__erikterm_preexec() {
  emulate -L zsh
  __erikterm_running=1
  __erikterm_prompt_marks_by_framework && return
  local REPLY
  __erikterm_urlencode "${1[1,2048]}"
  printf '\e]133;C;cmdline_url=%s\e\\' "$REPLY"
}

precmd_functions=(__erikterm_precmd_first ${precmd_functions:#__erikterm_precmd_*} __erikterm_precmd_last)
add-zsh-hook preexec __erikterm_preexec
__erikterm_wrap_prompt
