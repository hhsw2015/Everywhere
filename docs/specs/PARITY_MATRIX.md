# PARITY_MATRIX — agent-browser → Everywhere + OpenDia

Auto-rendered from `parity-matrix.json` (sha `ed2e10598c9064aecfaeb7cf21b540684db4be2c`).
DO NOT EDIT BY HAND. Run `node scripts/render-parity-matrix.mjs`.

## Summary

- total: **151**
- everywhere: **4**
- opendia: **105**
- universal: **15**
- wont-do: **27**

## Rows

| ab_command | tier | scope | ownership | our_tool | status | acceptance |
|---|---|---|---|---|---|---|
| agent_browser_auth_delete | value-add | in-browser | opendia | browser_auth_delete | missing | bench:auth_delete |
| agent_browser_auth_list | value-add | in-browser | opendia | browser_auth_list | missing | bench:auth_list |
| agent_browser_auth_login | value-add | in-browser | opendia | browser_auth_login | missing | manual:user |
| agent_browser_auth_save | value-add | in-browser | opendia | browser_auth_save | missing | manual:user |
| agent_browser_auth_show | value-add | in-browser | opendia | browser_auth_show | missing | bench:auth_show |
| agent_browser_back | core | in-browser | opendia | browser_back | in-progress | bench:back |
| agent_browser_batch | value-add | in-browser | universal | browser_batch | missing | bench:batch |
| agent_browser_chat | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_check | core | in-browser | opendia | browser_check | in-progress | bench:check |
| agent_browser_click | core | in-browser | opendia | browser_click | in-progress | bench:click |
| agent_browser_clipboard_copy | value-add | out-of-browser | everywhere | everywhere.clipboard_copy | missing | bench:clipboard_copy |
| agent_browser_clipboard_paste | value-add | out-of-browser | everywhere | everywhere.clipboard_paste | missing | bench:clipboard_paste |
| agent_browser_clipboard_read | value-add | out-of-browser | everywhere | everywhere.clipboard_read | missing | bench:clipboard_read |
| agent_browser_clipboard_write | value-add | out-of-browser | everywhere | everywhere.clipboard_write | missing | bench:clipboard_write |
| agent_browser_close | core | in-browser | opendia | browser_close | in-progress | bench:close |
| agent_browser_confirm | value-add | in-browser | opendia | browser_confirm | in-progress | bench:confirm |
| agent_browser_connect | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_console | value-add | in-browser | opendia | browser_console | in-progress | bench:console |
| agent_browser_cookies_clear | value-add | in-browser | opendia | browser_cookies_clear | in-progress | manual:user |
| agent_browser_cookies_get | value-add | in-browser | opendia | browser_cookies_get | in-progress | manual:user |
| agent_browser_cookies_set | value-add | in-browser | opendia | browser_cookies_set | missing | manual:user |
| agent_browser_cookies_set_curl | value-add | in-browser | opendia | browser_cookies_set_curl | missing | manual:user |
| agent_browser_dashboard_start | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_dashboard_stop | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_dblclick | niche | in-browser | opendia | browser_dblclick | in-progress | bench:dblclick |
| agent_browser_deny | value-add | in-browser | opendia | browser_deny | missing | bench:deny |
| agent_browser_device | niche | in-browser | opendia | browser_device | in-progress | bench:device |
| agent_browser_dialog_accept | value-add | in-browser | opendia | browser_dialog_accept | in-progress | bench:dialog_accept |
| agent_browser_dialog_dismiss | value-add | in-browser | opendia | browser_dialog_dismiss | in-progress | bench:dialog_dismiss |
| agent_browser_dialog_status | value-add | in-browser | opendia | browser_dialog_status | missing | bench:dialog_status |
| agent_browser_diff_screenshot | value-add | in-browser | universal | browser_diff_screenshot | missing | bench:diff_screenshot |
| agent_browser_diff_snapshot | value-add | in-browser | universal | browser_diff_snapshot | in-progress | bench:diff_snapshot |
| agent_browser_diff_url | value-add | in-browser | opendia | browser_diff_url | missing | bench:diff_url |
| agent_browser_doctor | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_download | value-add | both | opendia | browser_download | missing | bench:download |
| agent_browser_drag | niche | in-browser | opendia | browser_drag | in-progress | bench:drag |
| agent_browser_errors | value-add | in-browser | opendia | browser_errors | missing | bench:errors |
| agent_browser_eval | core | in-browser | opendia | browser_eval | missing | manual:user |
| agent_browser_fill | core | in-browser | opendia | browser_fill | in-progress | bench:fill |
| agent_browser_find | niche | in-browser | opendia | browser_find | in-progress | bench:find |
| agent_browser_focus | niche | in-browser | opendia | browser_focus | in-progress | bench:focus |
| agent_browser_forward | core | in-browser | opendia | browser_forward | in-progress | bench:forward |
| agent_browser_frame_main | value-add | in-browser | opendia | browser_frame_main | missing | bench:frame_main |
| agent_browser_frame_switch | value-add | in-browser | opendia | browser_frame_switch | missing | bench:frame_switch |
| agent_browser_get_attr | niche | in-browser | opendia | browser_get_attr | in-progress | bench:get_attr |
| agent_browser_get_box | niche | in-browser | opendia | browser_get_box | in-progress | bench:get_box |
| agent_browser_get_cdp_url | niche | in-browser | opendia | browser_get_cdp_url | in-progress | bench:get_cdp_url |
| agent_browser_get_count | niche | in-browser | opendia | browser_get_count | in-progress | bench:get_count |
| agent_browser_get_html | niche | in-browser | universal | browser_get_html | in-progress | bench:get_html |
| agent_browser_get_styles | niche | in-browser | opendia | browser_get_styles | in-progress | bench:get_styles |
| agent_browser_get_text | core | in-browser | universal | browser_get_text | in-progress | bench:get_text |
| agent_browser_get_title | core | in-browser | opendia | browser_get_title | in-progress | bench:get_title |
| agent_browser_get_url | core | in-browser | opendia | browser_get_url | in-progress | bench:get_url |
| agent_browser_get_value | niche | in-browser | opendia | browser_get_value | in-progress | bench:get_value |
| agent_browser_highlight | value-add | in-browser | universal | browser_highlight | missing | bench:highlight |
| agent_browser_hover | niche | in-browser | opendia | browser_hover | in-progress | bench:hover |
| agent_browser_inspect | value-add | in-browser | universal | browser_inspect | in-progress | bench:inspect |
| agent_browser_install | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_is_checked | niche | in-browser | opendia | browser_is_checked | in-progress | bench:is_checked |
| agent_browser_is_enabled | niche | in-browser | opendia | browser_is_enabled | in-progress | bench:is_enabled |
| agent_browser_is_visible | niche | in-browser | opendia | browser_is_visible | in-progress | bench:is_visible |
| agent_browser_keyboard_insert_text | niche | in-browser | opendia | browser_keyboard_insert_text | in-progress | bench:keyboard_insert_text |
| agent_browser_keyboard_type | niche | in-browser | opendia | browser_keyboard_type | in-progress | bench:keyboard_type |
| agent_browser_keydown | niche | in-browser | opendia | browser_keydown | in-progress | bench:keydown |
| agent_browser_keyup | niche | in-browser | opendia | browser_keyup | in-progress | bench:keyup |
| agent_browser_mouse_down | niche | in-browser | opendia | browser_mouse_down | in-progress | bench:mouse_down |
| agent_browser_mouse_move | niche | in-browser | opendia | browser_mouse_move | in-progress | bench:mouse_move |
| agent_browser_mouse_up | niche | in-browser | opendia | browser_mouse_up | in-progress | bench:mouse_up |
| agent_browser_mouse_wheel | niche | in-browser | opendia | browser_mouse_wheel | in-progress | bench:mouse_wheel |
| agent_browser_network_har_start | value-add | in-browser | opendia | browser_network_har_start | missing | manual:user |
| agent_browser_network_har_stop | value-add | in-browser | opendia | browser_network_har_stop | missing | manual:user |
| agent_browser_network_request | value-add | in-browser | opendia | browser_network_request | missing | bench:network_request |
| agent_browser_network_requests | value-add | in-browser | opendia | browser_network_requests | missing | bench:network_requests |
| agent_browser_network_route | value-add | in-browser | opendia | browser_network_route | missing | manual:user |
| agent_browser_network_unroute | value-add | in-browser | opendia | browser_network_unroute | missing | manual:user |
| agent_browser_open | core | in-browser | opendia | browser_open | in-progress | bench:open |
| agent_browser_pdf | value-add | both | opendia | browser_pdf | missing | bench:pdf |
| agent_browser_plugin_add | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_plugin_list | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_plugin_run | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_plugin_show | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_press | core | in-browser | opendia | browser_press | in-progress | bench:press |
| agent_browser_profiler_start | value-add | in-browser | opendia | browser_profiler_start | missing | bench:profiler_start |
| agent_browser_profiler_stop | value-add | in-browser | opendia | browser_profiler_stop | missing | bench:profiler_stop |
| agent_browser_profiles | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_pushstate | niche | in-browser | opendia | browser_pushstate | in-progress | bench:pushstate |
| agent_browser_react_inspect | niche | in-browser | opendia | browser_react_inspect | missing | bench:react_inspect |
| agent_browser_react_renders_start | niche | in-browser | opendia | browser_react_renders_start | missing | bench:react_renders_start |
| agent_browser_react_renders_stop | niche | in-browser | opendia | browser_react_renders_stop | missing | bench:react_renders_stop |
| agent_browser_react_suspense | niche | in-browser | opendia | browser_react_suspense | missing | bench:react_suspense |
| agent_browser_react_tree | niche | in-browser | opendia | browser_react_tree | missing | bench:react_tree |
| agent_browser_read | core | in-browser | universal | browser_read | blocked | bench:read |
| agent_browser_record_restart | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_record_start | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_record_stop | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_reload | core | in-browser | opendia | browser_reload | in-progress | bench:reload |
| agent_browser_remove_init_script | niche | in-browser | opendia | browser_remove_init_script | missing | manual:user |
| agent_browser_screenshot | core | both | universal | browser_screenshot | in-progress | bench:screenshot |
| agent_browser_scroll | core | in-browser | opendia | browser_scroll | in-progress | bench:scroll |
| agent_browser_scroll_into_view | niche | in-browser | opendia | browser_scroll_into_view | in-progress | bench:scroll_into_view |
| agent_browser_select | core | in-browser | opendia | browser_select | in-progress | bench:select |
| agent_browser_session | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_session_id | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_session_info | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_session_list | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_set_credentials | value-add | in-browser | opendia | browser_set_credentials | missing | manual:user |
| agent_browser_set_device | niche | in-browser | wont-do |  | wont-do | none |
| agent_browser_set_geo | niche | in-browser | opendia | browser_set_geo | in-progress | bench:set_geo |
| agent_browser_set_headers | value-add | in-browser | opendia | browser_set_headers | missing | manual:user |
| agent_browser_set_media | niche | in-browser | opendia | browser_set_media | in-progress | bench:set_media |
| agent_browser_set_offline | value-add | in-browser | opendia | browser_set_offline | missing | manual:user |
| agent_browser_set_viewport | niche | in-browser | opendia | browser_set_viewport | in-progress | bench:set_viewport |
| agent_browser_skills_get | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_skills_list | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_skills_path | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_snapshot | core | in-browser | universal | browser_snapshot | in-progress | bench:snapshot |
| agent_browser_state_clean | value-add | in-browser | opendia | browser_state_clean | missing | manual:user |
| agent_browser_state_clear | value-add | in-browser | opendia | browser_state_clear | missing | manual:user |
| agent_browser_state_list | value-add | in-browser | opendia | browser_state_list | missing | manual:user |
| agent_browser_state_load | value-add | in-browser | opendia | browser_state_load | missing | manual:user |
| agent_browser_state_rename | value-add | in-browser | opendia | browser_state_rename | missing | manual:user |
| agent_browser_state_save | value-add | in-browser | opendia | browser_state_save | missing | manual:user |
| agent_browser_state_show | value-add | in-browser | opendia | browser_state_show | missing | manual:user |
| agent_browser_storage_clear | value-add | in-browser | opendia | browser_storage_clear | missing | manual:user |
| agent_browser_storage_get | value-add | in-browser | opendia | browser_storage_get | missing | bench:storage_get |
| agent_browser_storage_set | value-add | in-browser | opendia | browser_storage_set | missing | manual:user |
| agent_browser_stream_disable | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_stream_enable | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_stream_status | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_swipe | niche | in-browser | opendia | browser_swipe | in-progress | bench:swipe |
| agent_browser_tab_close | core | in-browser | opendia | browser_tab_close | in-progress | bench:tab_close |
| agent_browser_tab_list | core | in-browser | opendia | browser_tab_list | in-progress | bench:tab_list |
| agent_browser_tab_new | core | in-browser | opendia | browser_tab_new | in-progress | bench:tab_new |
| agent_browser_tab_switch | core | in-browser | opendia | browser_tab_switch | in-progress | bench:tab_switch |
| agent_browser_tap | niche | in-browser | opendia | browser_tap | in-progress | bench:tap |
| agent_browser_tools_profiles | core | in-browser | wont-do |  | wont-do | none |
| agent_browser_trace_start | value-add | in-browser | opendia | browser_trace_start | missing | bench:trace_start |
| agent_browser_trace_stop | value-add | in-browser | opendia | browser_trace_stop | missing | bench:trace_stop |
| agent_browser_type | core | in-browser | opendia | browser_type | in-progress | bench:type |
| agent_browser_uncheck | core | in-browser | opendia | browser_uncheck | in-progress | bench:uncheck |
| agent_browser_upgrade | value-add | in-browser | wont-do |  | wont-do | none |
| agent_browser_upload | value-add | both | opendia | browser_upload | missing | bench:upload |
| agent_browser_vitals | niche | in-browser | opendia | browser_vitals | in-progress | bench:vitals |
| agent_browser_wait_for_download | value-add | in-browser | opendia | browser_wait_for_download | missing | bench:wait_for_download |
| agent_browser_wait_for_function | niche | in-browser | universal | browser_wait_for_function | in-progress | bench:wait_for_function |
| agent_browser_wait_for_load | core | in-browser | universal | browser_wait_for_load | in-progress | bench:wait_for_load |
| agent_browser_wait_for_selector | core | in-browser | universal | browser_wait_for_selector | in-progress | bench:wait_for_selector |
| agent_browser_wait_for_text | core | in-browser | universal | browser_wait_for_text | in-progress | bench:wait_for_text |
| agent_browser_wait_for_url | niche | in-browser | universal | browser_wait_for_url | in-progress | bench:wait_for_url |
| agent_browser_wait_ms | core | in-browser | opendia | browser_wait_ms | in-progress | bench:wait_ms |
| agent_browser_window_new | value-add | in-browser | opendia | browser_window_new | missing | bench:window_new |
