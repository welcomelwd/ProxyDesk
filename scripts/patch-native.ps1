$ErrorActionPreference = 'Stop'
$sourceRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'native/vendor/proxybridge/Windows/src'
function Edit-Source([string]$name, [scriptblock]$edit) {
    $sourcePath = Join-Path $sourceRoot $name
    $source = [IO.File]::ReadAllText($sourcePath)
    $next = & $edit $source
    [IO.File]::WriteAllText($sourcePath, $next, [Text.UTF8Encoding]::new($false))
}
Edit-Source 'pb_internal.h' {
    param($source)
    if ($source.Contains('#include "proxy_desk.h"')) { return $source }
    $source = $source.Replace('#include "windivert.h"', '#include "windivert.h"' + "`n" + '#include "proxy_desk.h"')
    $source = $source.Replace('SOCKET client_socket;', 'SOCKET client_socket; DWORD client_pid;')
    return $source
}
Edit-Source 'ProxyBridge.h' { param($source) $source.Replace('#define MAX_PROXY_CONFIGS 16', '#define MAX_PROXY_CONFIGS 256') }
Edit-Source 'pb_rules.c' {
    param($source)
    if ($source.Contains('pd_policy_callback(pid,')) { return $source }
    $source = $source.Replace('RuleAction action = match_rule_v6(process_name, dest_ip6, dest_port, is_udp, &proxy_config_id);', @'
    char destination[64];
    InetNtopA(AF_INET6, (void *)dest_ip6, destination, sizeof(destination));
    RuleAction action = pd_policy_callback
        ? (RuleAction)pd_policy_callback(pid, process_name, destination, dest_port, is_udp, &proxy_config_id)
        : RULE_ACTION_DIRECT;
'@)
    $source = $source.Replace('RuleAction action = match_rule(process_name, dest_ip, dest_port, is_udp, &proxy_config_id);', @'
    char destination[64];
    InetNtopA(AF_INET, &dest_ip, destination, sizeof(destination));
    RuleAction action = pd_policy_callback
        ? (RuleAction)pd_policy_callback(pid, process_name, destination, dest_port, is_udp, &proxy_config_id)
        : RULE_ACTION_DIRECT;
'@)
    $source = $source.Replace("return RULE_ACTION_DIRECT;  // No proxy configured", "return RULE_ACTION_BLOCK;  // Missing proxy must not fall back to direct.")
    $source = [regex]::Replace($source, '(if \(cfg == NULL \|\| cfg->host\[0\] == ''\\0'' \|\| cfg->port == 0\)\s*)return RULE_ACTION_DIRECT;', '$1return RULE_ACTION_BLOCK;')
    $source = [regex]::Replace($source, '(if \(is_udp && cfg->type == PROXY_TYPE_HTTP\)\s*)return RULE_ACTION_DIRECT;', '$1return RULE_ACTION_BLOCK;')
    return $source
}
Edit-Source 'pb_rules.c' {
    param($source)
    return $source.Replace('g_has_active_rules = has_active;', 'g_has_active_rules = has_active || pd_policy_callback != NULL;')
}
Edit-Source 'ProxyBridge.c' {
    param($source)
    if ($source.Contains('pd_handle_dns_packet(packet,')) { return $source }
    $source = $source.Replace('recv_fail_streak = 0;' + "`r`n`r`n" + '        PWINDIVERT_IPV6HDR', 'recv_fail_streak = 0;' + "`r`n        if (pd_handle_dns_packet(packet, packet_len, &addr)) continue;`r`n`r`n" + '        PWINDIVERT_IPV6HDR')
    if (-not $source.Contains('pd_handle_dns_packet(packet,')) {
        $source = $source.Replace('        PWINDIVERT_IPV6HDR ipv6_header = NULL;', '        if (pd_handle_dns_packet(packet, packet_len, &addr)) continue;' + "`n" + '        PWINDIVERT_IPV6HDR ipv6_header = NULL;')
    }
    return $source
}
Edit-Source 'ProxyBridge.c' {
    param($source)
    if ($source.Contains('Shared policy callback active')) { return $source }
    return $source.Replace('if (rule_count == 0)', 'if (pd_policy_callback)' + "`r`n" + '        log_message("Shared policy callback active");' + "`r`n" + '    else if (rule_count == 0)')
}
Edit-Source 'pb_proxy.c' {
    param($source)
    if (-not $source.Contains('if (pd_policy_callback) return TRUE;')) {
        $source = $source.Replace('BOOL is_proxy_config_referenced(UINT32 config_id)' + "`r`n{", 'BOOL is_proxy_config_referenced(UINT32 config_id)' + "`r`n{`r`n    if (pd_policy_callback) return TRUE;")
    }
    return $source
}
Edit-Source 'pb_relay.c' {
    param($source)
    if ($source.Contains('pd_tcp_dns(')) { return $source.Replace('get_process_id_from_connection(client_addr.sin_addr.s_addr, client_port)', 'get_process_id_from_connection(client_addr.sin_addr.s_addr, ntohs(client_addr.sin_port))').Replace('get_process_id_from_connection_v6((UINT8 *)&client_addr6.sin6_addr, client_port)', 'get_process_id_from_connection_v6((UINT8 *)&client_addr6.sin6_addr, ntohs(client_addr6.sin6_port))') }
    $source = $source.Replace('conn_config->client_socket = client_sock;', 'conn_config->client_socket = client_sock;' + "`n                    conn_config->client_pid = get_process_id_from_connection(client_addr.sin_addr.s_addr, client_port);")
    $source = $source.Replace('conn_config->client_socket = client_sock6;', 'conn_config->client_socket = client_sock6;' + "`n                    conn_config->client_pid = get_process_id_from_connection_v6((UINT8 *)&client_addr6.sin6_addr, client_port);")
    $source = $source.Replace('SOCKET client_sock = config->client_socket;', 'SOCKET client_sock = config->client_socket;' + "`n    if (config->orig_dest_port == 53 && pd_dns_callback) { DWORD dns_pid = config->client_pid; free(config); return pd_tcp_dns((uintptr_t)client_sock, dns_pid); }")
    return $source
}
Write-Output 'NATIVE_PATCH_PASS'
