-- erikterm + smart-splits.nvim: one set of keys for Neovim splits and erikterm panes.
--
-- erikterm's ActivatePaneDirection / AdjustPaneSize bindings pass the key through to the pane when
-- its IS_NVIM user var is "true". This module
--   * sets IS_NVIM (OSC 1337 SetUserVar) while Neovim runs in erikterm and clears it on exit/suspend;
--   * publishes NVIM_SERVER (v:servername), so erikterm opens Ctrl+clicked path:line links in this Neovim;
--   * hands moves and resizes past Neovim's outermost split back to erikterm via `erikterm cli`.
--
-- Install: copy (or symlink) this file to ~/.config/nvim/lua/erikterm.lua, then
--
--   local erikterm = require('erikterm')
--   erikterm.setup()                      -- the IS_NVIM user var; does nothing outside erikterm
--
-- then pick the line for your smart-splits version, keeping your other options and keymaps
-- (<C-h> = require('smart-splits').move_cursor_left, <A-h> = …resize_left, …):
--
--   -- smart-splits 1.x/2.x (`multiplexer_integration`): erikterm acts as a multiplexer, like
--   -- wezterm — moves past the last split and resizes of a full-width/height window hand off,
--   -- and at_edge = 'stop' | 'wrap' behaves as with tmux/wezterm.
--   require('smart-splits').setup({ multiplexer_integration = erikterm.multiplexer(), at_edge = 'stop' })
--
--   -- or only the move hand-off, through the at_edge callback (no resize hand-off unless your
--   -- <A-h> etc. call erikterm.resize('left') instead of smart-splits' resize_left):
--   require('smart-splits').setup({ at_edge = erikterm.at_edge })
--
--   -- smart-splits 3.x (backend protocol 3.0.0): erikterm is a backend
--   require('smart-splits').setup({ mux = { backend = { erikterm.backend } } })
--
-- `erikterm cli` finds the right window and pane from $ERIKTERM_SOCKET / $ERIKTERM_PANE, which
-- erikterm sets in every pane. Set `erikterm.cli = '/path/to/erikterm'` if it isn't on $PATH.

local M = {}

M.cli = vim.env.ERIKTERM_CLI or 'erikterm'

local DIRECTIONS = { left = 'Left', right = 'Right', up = 'Up', down = 'Down' }

--- True when running directly inside erikterm (not in tmux etc. inside it).
function M.inside()
  return vim.env.TERM_PROGRAM == 'erikterm' and vim.env.ERIKTERM_SOCKET ~= nil
end

local function write_terminal(seq)
  -- stderr is the terminal; this is what smart-splits itself uses for wezterm's user var.
  local ok = pcall(vim.api.nvim_chan_send, vim.v.stderr, seq)
  if not ok then
    io.stdout:write(seq)
    io.stdout:flush()
  end
end

--- OSC 1337 SetUserVar=NAME=base64(VALUE); an empty value clears it.
function M.set_user_var(name, value)
  local encoded = value == '' and '' or value == 'true' and 'dHJ1ZQ==' or vim.base64.encode(value)
  write_terminal(string.format('\027]1337;SetUserVar=%s=%s\007', name, encoded))
end

--- Runs `erikterm cli ARGS...`; returns (ok, stdout). Never throws; gives up after 500 ms.
function M.run(args)
  local cmd = { M.cli, 'cli' }
  vim.list_extend(cmd, args)
  if vim.system then
    local ok, result = pcall(function()
      return vim.system(cmd, { text = true, timeout = 500 }):wait()
    end)
    if not ok or result.code ~= 0 then return false, '' end
    return true, result.stdout or ''
  end
  local out = vim.fn.system(cmd)
  return vim.v.shell_error == 0, out
end

--- Moves to the erikterm pane in `direction` ('left'|'right'|'up'|'down'). False at erikterm's edge too.
function M.move(direction)
  local dir = DIRECTIONS[direction]
  if not dir or not M.inside() then return false end
  local ok, id = M.run({ 'get-pane-direction', dir })
  id = vim.trim(id or '')
  if not ok or id == '' then return false end
  return (M.run({ 'activate-pane', '--pane-id', id }))
end

--- Moves erikterm's pane edge `amount` cells (default 3) towards `direction`.
function M.resize_pane(direction, amount)
  local dir = DIRECTIONS[direction]
  if not dir or not M.inside() then return false end
  return (M.run({ 'adjust-pane-size', '--amount', tostring(amount or 3), dir }))
end

--- smart-splits 2.x `at_edge` callback: past Neovim's last split, continue into erikterm's panes.
--- ctx.direction is 'left'|'right'|'up'|'down'; ctx.wrap / ctx.split are smart-splits' fallbacks.
function M.at_edge(ctx)
  if not M.move(ctx.direction) and M.on_last_edge then M.on_last_edge(ctx) end
end

--- Optional: what M.at_edge does at erikterm's edge too, e.g. `function(ctx) ctx.wrap() end`. Default: stop.
M.on_last_edge = nil

local OPPOSITE = { left = 'l', right = 'h', up = 'j', down = 'k' }
local SAME = { left = 'h', right = 'l', up = 'k', down = 'j' }

--- For smart-splits 2.x keymaps: resize Neovim's split, or erikterm's pane when the current window
--- spans the whole Neovim layout along that axis (nothing to resize inside Neovim).
function M.resize(direction, amount)
  amount = (amount or 3) * vim.v.count1
  local cur = vim.fn.winnr()
  local fills = vim.fn.winnr(SAME[direction]) == cur and vim.fn.winnr(OPPOSITE[direction]) == cur
  if fills and M.inside() then
    M.resize_pane(direction, amount)
  else
    require('smart-splits')['resize_' .. direction](amount)
  end
end

--- smart-splits 3.x backend (protocol 3.0.0: detect / move / resize / activate).
M.backend = {
  name = 'erikterm',
  protocol_version = '^3.0.0',
  detect = function() return M.inside() end,
  move = function(direction, _opts) return M.move(direction) end,
  resize = function(direction, opts)
    return M.resize_pane(direction, opts and opts.amount or 3)
  end,
  activate = function() M.setup() end,
  health = function()
    local ok = M.run({ 'list' })
    if ok then
      vim.health.ok('erikterm cli reaches this pane (' .. (vim.env.ERIKTERM_PANE or '?') .. ')')
    else
      vim.health.error('`' .. M.cli .. ' cli list` failed; is erikterm on $PATH? (set require("erikterm").cli)')
    end
  end,
}

--- smart-splits 1.x/2.x multiplexer (SmartSplitsMultiplexer in its types.lua); smart-splits loads it
--- as require('smart-splits.mux.erikterm') when multiplexer_integration = 'erikterm'.
M.mux = {
  type = 'erikterm',
  is_in_session = function() return M.inside() end,
  current_pane_id = function() return tonumber(vim.env.ERIKTERM_PANE) end,
  current_pane_at_edge = function(direction)
    local ok, id = M.run({ 'get-pane-direction', DIRECTIONS[direction] or 'Left' })
    return not ok or vim.trim(id or '') == ''
  end,
  current_pane_is_zoomed = function()
    local ok, out = M.run({ 'list', '--format', 'json' })
    if not ok then return false end
    local decoded, panes = pcall(vim.json.decode, out)
    if not decoded then return false end
    for _, pane in ipairs(panes) do
      if tostring(pane.pane_id) == vim.env.ERIKTERM_PANE then return pane.is_zoomed == true end
    end
    return false
  end,
  next_pane = function(direction) return M.move(direction) end,
  resize_pane = function(direction, amount) return M.resize_pane(direction, amount) end,
  split_pane = function(direction, _size)
    local flags = { left = '--left', right = '--right', up = '--top', down = '--bottom' }
    return (M.run({ 'split-pane', flags[direction] or '--right' }))
  end,
  update_mux_layout_details = function() end,
}

package.preload['smart-splits.mux.erikterm'] = function() return M.mux end

--- Value for smart-splits' multiplexer_integration: 'erikterm' inside erikterm, else nil (auto-detect).
function M.multiplexer()
  return M.inside() and 'erikterm' or nil
end

local did_setup = false

--- Sets IS_NVIM=true while Neovim runs (and is in the foreground) inside erikterm, and NVIM_SERVER to its RPC
--- socket (v:servername): Ctrl+click on a path:line in another pane of the tab then opens the file in this Neovim
--- (`nvim --server … --remote-send`) instead of starting a new one.
function M.setup()
  if did_setup or vim.env.TERM_PROGRAM ~= 'erikterm' then return end
  did_setup = true
  local group = vim.api.nvim_create_augroup('erikterm_is_nvim', { clear = true })
  local function on()
    M.set_user_var('IS_NVIM', 'true')
    local server = vim.v.servername
    if (server == nil or server == '') and vim.fn.serverstart then
      local ok, started = pcall(vim.fn.serverstart)
      if ok then server = started end
    end
    if server and server ~= '' then M.set_user_var('NVIM_SERVER', server) end
  end
  local function off()
    M.set_user_var('IS_NVIM', '')
    M.set_user_var('NVIM_SERVER', '')
  end
  vim.api.nvim_create_autocmd({ 'VimEnter', 'VimResume' }, { group = group, callback = on })
  vim.api.nvim_create_autocmd({ 'VimLeavePre', 'VimSuspend' }, { group = group, callback = off })
  if vim.v.vim_did_enter == 1 then on() end -- setup() called lazily, after VimEnter
end

return M
