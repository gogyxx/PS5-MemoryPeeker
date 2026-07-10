#include <arpa/inet.h>
#include <ctype.h>
#include <errno.h>
#include <netinet/in.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/ptrace.h>
#include <sys/types.h>
#include <sys/proc.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <sys/sysctl.h>
#include <sys/user.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#include <ps5/kernel.h>
#include <ps5/mdbg.h>

#include "web_ui.h"
#include "audio_assets.h"

#define HTTP_PORT 1999
#define REQ_BUFFER_SIZE 4096
#define SCAN_CHUNK_SIZE (1024 * 1024)
#define MAX_SCAN_RESULTS 100000
#define MAX_SECTION_SCAN_BYTES (256ULL * 1024ULL * 1024ULL)
#define MAX_MEMORY_SECTIONS 1024
#define MAX_VALUE_BYTES 32
#define WPF_SECTION_CHUNK_SIZE (128ULL * 1024ULL * 1024ULL)
#define GAME_MEMORY_CEILING 0x1000000000ULL
#define SCE_NOTIFICATION_LOCAL_USER_ID_SYSTEM 0xFE

typedef struct app_info {
    uint32_t app_id;
    uint64_t unknown1;
    char title_id[14];
    char unknown2[0x3c];
} app_info_t;

typedef struct eboot_info {
    pid_t pid;
    char title_id[16];
    char command[32];
    bool found;
} eboot_info_t;

typedef struct scan_result {
    uint64_t address;
    uint64_t section_start;
    uint8_t bytes[MAX_VALUE_BYTES];
    int size;
    int type;
    char section[96];
} scan_result_t;

typedef struct memory_section {
    uint64_t start;
    uint64_t end;
    int protection;
    int index;
    int score;
    bool selected;
    char kind[24];
    char name[128];
    char display[192];
} memory_section_t;

typedef enum scan_state {
    SCAN_IDLE,
    SCAN_RUNNING,
    SCAN_COMPLETED,
    SCAN_CANCELLED,
    SCAN_ERROR
} scan_state_t;

typedef enum value_kind {
    VALUE_U8 = 1,
    VALUE_U16 = 2,
    VALUE_U32 = 4,
    VALUE_U64 = 8,
    VALUE_FLOAT = 104,
    VALUE_DOUBLE = 108,
    VALUE_HEX = 200,
    VALUE_STRING = 201
} value_kind_t;

typedef enum compare_kind {
    COMPARE_EXACT,
    COMPARE_FUZZY,
    COMPARE_INCREASED,
    COMPARE_INCREASED_BY,
    COMPARE_DECREASED,
    COMPARE_DECREASED_BY,
    COMPARE_BIGGER,
    COMPARE_SMALLER,
    COMPARE_CHANGED,
    COMPARE_UNCHANGED,
    COMPARE_BETWEEN,
    COMPARE_UNKNOWN
} compare_kind_t;

typedef struct value_spec {
    value_kind_t kind;
    compare_kind_t compare;
    int size;
    uint8_t first[MAX_VALUE_BYTES];
    uint8_t second[MAX_VALUE_BYTES];
} value_spec_t;

typedef struct scan_job {
    scan_state_t state;
    int mode;
    pid_t pid;
    volatile bool cancel;
    bool capped;
    bool auto_pause;
    uint64_t total_units;
    uint64_t completed_units;
    size_t matches;
    uint64_t started_ms;
    uint64_t finished_ms;
    char message[128];
    char error[128];
} scan_job_t;

typedef struct scan_task {
    int mode;
    pid_t pid;
    bool aligned;
    bool auto_pause;
    value_spec_t spec;
    memory_section_t *sections;
    size_t section_count;
    scan_result_t *previous;
    size_t previous_count;
} scan_task_t;

int sceNotificationSend(int userId, bool isLogged, const char *payload);
int sceKernelGetAppInfo(pid_t pid, app_info_t *info);

static volatile int g_running = 1;
static int g_last_errno = 0;
static scan_result_t *g_results = NULL;
static size_t g_result_count = 0;
static int g_result_size = 4;
static int g_result_type = VALUE_U32;
static memory_section_t g_sections[MAX_MEMORY_SECTIONS];
static size_t g_section_count = 0;
static size_t g_raw_section_count = 0;
static size_t g_merged_section_count = 0;
static pid_t g_section_pid = -1;
static scan_job_t g_scan = { .state = SCAN_IDLE };
static pthread_mutex_t g_state_lock = PTHREAD_MUTEX_INITIALIZER;
static pid_t g_paused_pid = -1;

static bool parse_value_kind(const char *text, value_kind_t *kind);
static bool encode_value(value_kind_t kind, const char *text, uint8_t out[MAX_VALUE_BYTES], int *size);
static void result_value_text(const scan_result_t *result, char *out, size_t out_size);

static int privileged_ptrace(int request, pid_t pid, caddr_t addr, int data)
{
    uint8_t privcaps[16] = {
        0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
        0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff
    };
    pid_t mypid = getpid();
    uint8_t caps[16];
    uint64_t authid;
    int ret;

    authid = kernel_get_ucred_authid(mypid);
    if (!authid || kernel_get_ucred_caps(mypid, caps)) {
        return -1;
    }
    if (kernel_set_ucred_authid(mypid, 0x4800000000010003l) ||
        kernel_set_ucred_caps(mypid, privcaps)) {
        return -1;
    }

    ret = (int)__syscall(SYS_ptrace, request, pid, addr, data);

    kernel_set_ucred_authid(mypid, authid);
    kernel_set_ucred_caps(mypid, caps);
    return ret;
}

static int ptrace_write_memory(pid_t pid, uint64_t addr, const uint8_t *bytes, size_t len)
{
    struct ptrace_io_desc iod;
    int attached = 0;
    int rc = -1;

    memset(&iod, 0, sizeof(iod));

    if (privileged_ptrace(PT_ATTACH, pid, 0, 0)) {
        return -1;
    }
    attached = 1;
    waitpid(pid, 0, 0);

    iod.piod_op = PIOD_WRITE_D;
    iod.piod_offs = (void *)addr;
    iod.piod_addr = (void *)bytes;
    iod.piod_len = len;
    rc = privileged_ptrace(PT_IO, pid, (caddr_t)&iod, 0);

    if (attached) {
        privileged_ptrace(PT_DETACH, pid, 0, 0);
    }
    return rc;
}

static const char *toast_prefix =
    "{"
    "\"rawData\":{"
    "\"viewTemplateType\":\"InteractiveToastTemplateB\","
    "\"channelType\":\"Downloads\","
    "\"useCaseId\":\"IDC\","
    "\"toastOverwriteType\":\"No\","
    "\"isImmediate\":true,"
    "\"priority\":100,"
    "\"viewData\":{"
    "\"message\":{\"body\":\"";

static const char *toast_suffix =
    "\"},"
    "\"subMessage\":{\"body\":\"PS5MemoryPeeker Web\"}"
    "},"
    "\"platformViews\":{"
    "\"previewDisabled\":{"
    "\"viewData\":{"
    "\"message\":{\"body\":\"PS5MemoryPeeker Web\"}"
    "}"
    "}"
    "}"
    "},"
    "\"createdDateTime\":\"2026-07-10T00:00:00.000Z\","
    "\"localNotificationId\":\"1999\""
    "}";

static void notify_ps5(const char *message)
{
    char payload[2048];
    snprintf(payload, sizeof(payload), "%s%s%s", toast_prefix, message, toast_suffix);
    sceNotificationSend(SCE_NOTIFICATION_LOCAL_USER_ID_SYSTEM, true, payload);
}

static void write_all(int fd, const char *data)
{
    size_t len = strlen(data);
    while (len > 0) {
        ssize_t written = write(fd, data, len);
        if (written <= 0) {
            return;
        }
        data += written;
        len -= (size_t)written;
    }
}

static void send_header(int client, const char *status, const char *content_type)
{
    char header[512];
    snprintf(header, sizeof(header),
        "HTTP/1.1 %s\r\n"
        "Server: PS5MemoryPeeker-Web\r\n"
        "Content-Type: %s\r\n"
        "Connection: close\r\n"
        "Access-Control-Allow-Origin: *\r\n"
        "\r\n",
        status,
        content_type);
    write_all(client, header);
}

static void send_response(int client, const char *status, const char *content_type, const char *body)
{
    char header[512];
    snprintf(header, sizeof(header),
        "HTTP/1.1 %s\r\n"
        "Server: PS5MemoryPeeker-Web\r\n"
        "Content-Type: %s\r\n"
        "Content-Length: %d\r\n"
        "Connection: close\r\n"
        "Access-Control-Allow-Origin: *\r\n"
        "\r\n",
        status,
        content_type,
        (int)strlen(body));

    write_all(client, header);
    write_all(client, body);
}

static void send_response_bytes(int client, const char *status, const char *content_type, const unsigned char *body, size_t length)
{
    char header[512];
    snprintf(header, sizeof(header),
        "HTTP/1.1 %s\r\n"
        "Server: PS5MemoryPeeker-Web\r\n"
        "Content-Type: %s\r\n"
        "Content-Length: %llu\r\n"
        "Cache-Control: no-store\r\n"
        "Connection: close\r\n"
        "Access-Control-Allow-Origin: *\r\n"
        "\r\n",
        status,
        content_type,
        (unsigned long long)length);
    write_all(client, header);

    while (length > 0) {
        ssize_t written = write(client, body, length);
        if (written <= 0) {
            return;
        }
        body += written;
        length -= (size_t)written;
    }
}

static int hex_value(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static bool parse_hex_bytes(const char *hex, uint8_t *out, int *out_len, int max_len)
{
    int len = 0;
    if (hex[0] == '0' && (hex[1] == 'x' || hex[1] == 'X')) {
        hex += 2;
    }

    while (*hex) {
        if (*hex == '%' || *hex == ',' || *hex == ' ' || *hex == '-') {
            hex++;
            continue;
        }
        int hi = hex_value(*hex++);
        if (hi < 0 || !*hex) return false;
        int lo = hex_value(*hex++);
        if (lo < 0 || len >= max_len) return false;
        out[len++] = (uint8_t)((hi << 4) | lo);
    }

    *out_len = len;
    return len > 0;
}

static bool parse_u64(const char *text, uint64_t *value)
{
    char *end = NULL;
    int base = 10;
    if (text && text[0] == '0' && (text[1] == 'x' || text[1] == 'X')) {
        base = 16;
    }
    errno = 0;
    unsigned long long parsed = strtoull(text ? text : "", &end, base);
    if (errno || end == text) {
        return false;
    }
    *value = (uint64_t)parsed;
    return true;
}

static int query_get(const char *query, const char *key, char *out, size_t out_size)
{
    if (!query || !key || !out || out_size == 0) {
        return 0;
    }

    size_t key_len = strlen(key);
    const char *p = query;
    while (*p) {
        if (!strncmp(p, key, key_len) && p[key_len] == '=') {
            p += key_len + 1;
            size_t n = 0;
            while (*p && *p != '&' && n + 1 < out_size) {
                if (*p == '+') {
                    out[n++] = ' ';
                    p++;
                } else if (*p == '%' && isxdigit((unsigned char)p[1]) && isxdigit((unsigned char)p[2])) {
                    int hi = hex_value(p[1]);
                    int lo = hex_value(p[2]);
                    out[n++] = (char)((hi << 4) | lo);
                    p += 3;
                } else {
                    out[n++] = *p++;
                }
            }
            out[n] = 0;
            return 1;
        }
        while (*p && *p != '&') p++;
        if (*p == '&') p++;
    }
    return 0;
}

static void json_string(int client, const char *text)
{
    write_all(client, "\"");
    for (const char *p = text ? text : ""; *p; p++) {
        char tmp[8];
        if (*p == '"' || *p == '\\') {
            tmp[0] = '\\';
            tmp[1] = *p;
            tmp[2] = 0;
            write_all(client, tmp);
        } else if ((unsigned char)*p < 0x20) {
            write_all(client, " ");
        } else {
            tmp[0] = *p;
            tmp[1] = 0;
            write_all(client, tmp);
        }
    }
    write_all(client, "\"");
}

static bool find_eboot(eboot_info_t *info)
{
    int mib[4] = { CTL_KERN, KERN_PROC, KERN_PROC_PROC, 0 };
    size_t buf_size = 0;
    void *buf = NULL;

    memset(info, 0, sizeof(*info));
    info->pid = -1;

    if (sysctl(mib, 4, NULL, &buf_size, NULL, 0)) {
        g_last_errno = errno;
        return false;
    }

    buf = malloc(buf_size);
    if (!buf) {
        g_last_errno = errno;
        return false;
    }

    if (sysctl(mib, 4, buf, &buf_size, NULL, 0)) {
        g_last_errno = errno;
        free(buf);
        return false;
    }

    for (uint8_t *ptr = (uint8_t *)buf; ptr < ((uint8_t *)buf + buf_size);) {
        struct kinfo_proc *ki = (struct kinfo_proc *)ptr;
        if (ki->ki_structsize <= 0) {
            break;
        }

        if (!strcmp(ki->ki_comm, "eboot.bin")) {
            app_info_t app = {0};
            info->pid = ki->ki_pid;
            snprintf(info->command, sizeof(info->command), "%s", ki->ki_comm);
            if (!sceKernelGetAppInfo(ki->ki_pid, &app)) {
                snprintf(info->title_id, sizeof(info->title_id), "%s", app.title_id);
            }
            info->found = true;
        }

        ptr += ki->ki_structsize;
    }

    free(buf);
    return info->found;
}

static const char *section_kind(const struct kinfo_vmentry *entry)
{
    if ((entry->kve_protection & KVME_PROT_EXEC) == KVME_PROT_EXEC) {
        return "executable";
    }
    if (entry->kve_path[0]) {
        return "file";
    }
    return "anon";
}

static void section_name(const struct kinfo_vmentry *entry, char *out, size_t out_size)
{
    const char *kind = section_kind(entry);
    uint64_t kb = (entry->kve_end > entry->kve_start) ? (entry->kve_end - entry->kve_start) / 1024 : 0;
    if (entry->kve_path[0]) {
        snprintf(out, out_size, "%s:%s", kind, entry->kve_path);
    } else {
        snprintf(out, out_size, "%s[0]-%llX-%lluKB", kind,
            (unsigned long long)entry->kve_start,
            (unsigned long long)kb);
    }
}

static bool section_matches_filter(const struct kinfo_vmentry *entry, const char *filter)
{
    char name[256];
    if (!filter || !filter[0]) {
        return true;
    }
    section_name(entry, name, sizeof(name));
    return strstr(name, filter) != NULL || strstr(section_kind(entry), filter) != NULL;
}

typedef bool (*map_entry_cb)(const struct kinfo_vmentry *entry, void *ctx);

static int each_vm_entry(pid_t pid, map_entry_cb cb, void *ctx)
{
    int mib[4] = { CTL_KERN, KERN_PROC, KERN_PROC_VMMAP, pid };
    size_t buf_size = 0;
    void *buf = NULL;

    if (sysctl(mib, 4, NULL, &buf_size, NULL, 0)) {
        g_last_errno = errno;
        return -1;
    }

    buf = malloc(buf_size);
    if (!buf) {
        g_last_errno = errno;
        return -2;
    }

    if (sysctl(mib, 4, buf, &buf_size, NULL, 0)) {
        g_last_errno = errno;
        free(buf);
        return -3;
    }

    for (uint8_t *ptr = (uint8_t *)buf; ptr < ((uint8_t *)buf + buf_size);) {
        struct kinfo_vmentry *entry = (struct kinfo_vmentry *)ptr;
        if (entry->kve_structsize <= 0) {
            break;
        }
        if (!cb(entry, ctx)) {
            break;
        }
        ptr += entry->kve_structsize;
    }

    free(buf);
    return 0;
}

static void value_to_bytes(uint64_t value, int size, uint8_t out[8])
{
    for (int i = 0; i < size; i++) {
        out[i] = (uint8_t)((value >> (i * 8)) & 0xff);
    }
}

static uint64_t bytes_to_value(const uint8_t *bytes, int size)
{
    uint64_t value = 0;
    for (int i = 0; i < size; i++) {
        value |= ((uint64_t)bytes[i]) << (i * 8);
    }
    return value;
}

static void bytes_to_hex(const uint8_t *bytes, int len, char *out, size_t out_size)
{
    size_t used = 0;
    for (int i = 0; i < len && used + 2 < out_size; i++) {
        used += (size_t)snprintf(out + used, out_size - used, "%02X", bytes[i]);
    }
}

static void send_eboot(int client)
{
    eboot_info_t eboot;
    char body[256];
    bool found = find_eboot(&eboot);
    snprintf(body, sizeof(body),
        "{\"found\":%s,\"pid\":%d,\"titleId\":\"%s\",\"command\":\"%s\",\"lastErrno\":%d}",
        found ? "true" : "false",
        found ? eboot.pid : -1,
        eboot.title_id,
        eboot.command,
        g_last_errno);
    send_response(client, "200 OK", "application/json", body);
}

static bool contains_ci(const char *haystack, const char *needle)
{
    if (!haystack || !needle || !needle[0]) {
        return false;
    }
    size_t needle_len = strlen(needle);
    for (const char *p = haystack; *p; p++) {
        size_t i = 0;
        while (i < needle_len && p[i] && tolower((unsigned char)p[i]) == tolower((unsigned char)needle[i])) {
            i++;
        }
        if (i == needle_len) {
            return true;
        }
    }
    return false;
}

static bool looks_like_library(const char *name)
{
    return contains_ci(name, ".sprx") || contains_ci(name, ".prx") || contains_ci(name, ".so") ||
        contains_ci(name, ".elf") || contains_ci(name, "/lib") || contains_ci(name, "libkernel") ||
        contains_ci(name, "libsce");
}

static int score_section_name(const char *name, int protection, uint64_t length)
{
    int score = 20;
    if (contains_ci(name, "eboot") || contains_ci(name, "/app0/")) score += 60;
    if (contains_ci(name, "anon") || contains_ci(name, "dlmalloc") || contains_ci(name, "heap") || contains_ci(name, "game")) score += 55;
    if ((protection & KVME_PROT_WRITE) == KVME_PROT_WRITE) score += 25;
    if ((protection & KVME_PROT_EXEC) == KVME_PROT_EXEC) score -= 20;
    if (looks_like_library(name)) score -= 90;
    if (length < 4096) score -= 15;
    if (length > 1024ULL * 1024ULL * 1024ULL) score -= 20;
    if (score < 0) score = 0;
    if (score > 100) score = 100;
    return score;
}

static const char *classify_section_name(const char *name, int protection)
{
    if (contains_ci(name, "eboot") || contains_ci(name, "/app0/")) return "Game image";
    if (contains_ci(name, "anon") || contains_ci(name, "dlmalloc") || contains_ci(name, "heap") || contains_ci(name, "game")) return "Game heap";
    if (looks_like_library(name)) return "Library";
    if ((protection & KVME_PROT_WRITE) == KVME_PROT_WRITE) return "Writable";
    return "Mapped";
}

typedef struct section_collect_ctx {
    memory_section_t old[MAX_MEMORY_SECTIONS];
    size_t old_count;
} section_collect_ctx_t;

static bool previous_selection(const section_collect_ctx_t *ctx, uint64_t start, bool fallback)
{
    for (size_t i = 0; i < ctx->old_count; i++) {
        if (ctx->old[i].start == start) return ctx->old[i].selected;
    }
    return fallback;
}

static bool collect_section_cb(const struct kinfo_vmentry *entry, void *ctx_ptr)
{
    section_collect_ctx_t *ctx = (section_collect_ctx_t *)ctx_ptr;
    if ((entry->kve_protection & KVME_PROT_READ) != KVME_PROT_READ || entry->kve_end <= entry->kve_start) {
        return true;
    }

    g_raw_section_count++;
    bool executable = (entry->kve_protection & KVME_PROT_EXEC) == KVME_PROT_EXEC;
    bool named = entry->kve_path[0] != 0;
    bool low_game_memory = !named && entry->kve_start < GAME_MEMORY_CEILING;
    if (!named && !executable && !low_game_memory) {
        return true;
    }
    if (g_section_count >= MAX_MEMORY_SECTIONS) return false;
    const char *base_name = named ? entry->kve_path : (executable ? "executable" : "Game memory");
    uint64_t length = entry->kve_end - entry->kve_start;

    if (g_section_count > 0) {
        memory_section_t *previous = &g_sections[g_section_count - 1];
        if (previous->end == entry->kve_start && previous->protection == entry->kve_protection && !strcmp(previous->name, base_name)) {
            previous->end = entry->kve_end;
            length = previous->end - previous->start;
            previous->score = score_section_name(previous->name, previous->protection, length);
            snprintf(previous->display, sizeof(previous->display), "%s[0]-%X-%llX-%lluKB",
                section_kind(entry), previous->protection,
                (unsigned long long)previous->start, (unsigned long long)(length / 1024));
            g_merged_section_count++;
            return true;
        }
    }

    memory_section_t *section = &g_sections[g_section_count];
    memset(section, 0, sizeof(*section));
    section->start = entry->kve_start;
    section->end = entry->kve_end;
    section->protection = entry->kve_protection;
    section->index = (int)g_section_count;
    snprintf(section->name, sizeof(section->name), "%s", base_name);
    snprintf(section->kind, sizeof(section->kind), "%s", classify_section_name(base_name, entry->kve_protection));
    section->score = score_section_name(base_name, entry->kve_protection, length);
    bool default_selected = section->score >= 45;
    if (low_game_memory && (entry->kve_protection & KVME_PROT_WRITE) != KVME_PROT_WRITE) default_selected = false;
    section->selected = previous_selection(ctx, section->start, default_selected);
    snprintf(section->display, sizeof(section->display), "%s[0]-%X-%llX-%lluKB",
        section_kind(entry), entry->kve_protection,
        (unsigned long long)section->start, (unsigned long long)(length / 1024));
    g_section_count++;
    return g_section_count < MAX_MEMORY_SECTIONS;
}

static int load_memory_sections(pid_t pid)
{
    section_collect_ctx_t ctx;
    memset(&ctx, 0, sizeof(ctx));

    pthread_mutex_lock(&g_state_lock);
    if (g_section_pid == pid) {
        ctx.old_count = g_section_count;
        memcpy(ctx.old, g_sections, g_section_count * sizeof(memory_section_t));
    }
    g_section_count = 0;
    g_raw_section_count = 0;
    g_merged_section_count = 0;
    int rc = each_vm_entry(pid, collect_section_cb, &ctx);
    if (rc == 0) g_section_pid = pid;
    pthread_mutex_unlock(&g_state_lock);
    return rc;
}

static void send_maps(int client, const char *query)
{
    eboot_info_t eboot;
    char refresh_text[8];
    bool refresh = query_get(query, "refresh", refresh_text, sizeof(refresh_text)) && atoi(refresh_text) != 0;
    if (!find_eboot(&eboot)) {
        send_response(client, "404 Not Found", "application/json", "{\"error\":\"eboot_not_found\",\"message\":\"No running eboot.bin process was found.\"}");
        return;
    }
    if (refresh || g_section_pid != eboot.pid || g_section_count == 0) {
        int rc = load_memory_sections(eboot.pid);
        if (rc != 0) {
            send_response(client, "500 Internal Server Error", "application/json", "{\"error\":\"memory_map_failed\"}");
            return;
        }
    }

    send_header(client, "200 OK", "application/json");
    char head[160];
    size_t selected = 0;
    pthread_mutex_lock(&g_state_lock);
    for (size_t i = 0; i < g_section_count; i++) if (g_sections[i].selected) selected++;
    snprintf(head, sizeof(head), "{\"pid\":%d,\"selected\":%llu,\"total\":%llu,\"raw\":%llu,\"merged\":%llu,\"sections\":[",
        eboot.pid, (unsigned long long)selected, (unsigned long long)g_section_count,
        (unsigned long long)g_raw_section_count, (unsigned long long)g_merged_section_count);
    write_all(client, head);
    for (size_t i = 0; i < g_section_count; i++) {
        memory_section_t *section = &g_sections[i];
        char tmp[384];
        if (i) write_all(client, ",");
        snprintf(tmp, sizeof(tmp),
            "{\"index\":%d,\"start\":\"0x%llX\",\"end\":\"0x%llX\",\"length\":%llu,\"prot\":%d,\"score\":%d,\"selected\":%s,\"kind\":",
            section->index, (unsigned long long)section->start, (unsigned long long)section->end,
            (unsigned long long)(section->end - section->start), section->protection, section->score,
            section->selected ? "true" : "false");
        write_all(client, tmp);
        json_string(client, section->kind);
        write_all(client, ",\"name\":");
        json_string(client, section->name);
        write_all(client, ",\"display\":");
        json_string(client, section->display);
        write_all(client, "}");
    }
    pthread_mutex_unlock(&g_state_lock);
    write_all(client, "]}");
}

static void send_section_select(int client, const char *query, bool all)
{
    char selected_text[8], index_text[32];
    bool selected = query_get(query, "selected", selected_text, sizeof(selected_text)) && atoi(selected_text) != 0;
    pthread_mutex_lock(&g_state_lock);
    if (all) {
        for (size_t i = 0; i < g_section_count; i++) g_sections[i].selected = selected;
    } else {
        uint64_t index = 0;
        if (!query_get(query, "index", index_text, sizeof(index_text)) || !parse_u64(index_text, &index) || index >= g_section_count) {
            pthread_mutex_unlock(&g_state_lock);
            send_response(client, "400 Bad Request", "application/json", "{\"error\":\"bad_section_index\"}");
            return;
        }
        g_sections[index].selected = selected;
    }
    size_t count = 0;
    for (size_t i = 0; i < g_section_count; i++) if (g_sections[i].selected) count++;
    pthread_mutex_unlock(&g_state_lock);
    char body[128];
    snprintf(body, sizeof(body), "{\"ok\":true,\"selected\":%s,\"count\":%llu}", selected ? "true" : "false", (unsigned long long)count);
    send_response(client, "200 OK", "application/json", body);
}

static void send_read(int client, const char *query)
{
    eboot_info_t eboot;
    char addr_text[64], len_text[32], type_text[24] = "hex";
    uint64_t addr = 0, len64 = 0;
    if (!find_eboot(&eboot)) {
        send_response(client, "404 Not Found", "application/json", "{\"error\":\"eboot_not_found\"}");
        return;
    }
    if (!query_get(query, "addr", addr_text, sizeof(addr_text)) || !parse_u64(addr_text, &addr) ||
        !query_get(query, "len", len_text, sizeof(len_text)) || !parse_u64(len_text, &len64) ||
        len64 == 0 || len64 > 4096) {
        send_response(client, "400 Bad Request", "application/json", "{\"error\":\"bad_addr_or_len\"}");
        return;
    }

    uint8_t *buf = malloc((size_t)len64);
    char *hex = malloc((size_t)len64 * 2 + 1);
    if (!buf || !hex) {
        free(buf);
        free(hex);
        send_response(client, "500 Internal Server Error", "application/json", "{\"error\":\"oom\"}");
        return;
    }

    if (mdbg_copyout(eboot.pid, (intptr_t)addr, buf, (size_t)len64)) {
        g_last_errno = errno;
        free(buf);
        free(hex);
        send_response(client, "500 Internal Server Error", "application/json", "{\"error\":\"read_failed\"}");
        return;
    }

    bytes_to_hex(buf, (int)len64, hex, (size_t)len64 * 2 + 1);
    query_get(query, "type", type_text, sizeof(type_text));
    value_kind_t kind = VALUE_HEX;
    parse_value_kind(type_text, &kind);
    char value[160] = "";
    if (len64 <= MAX_VALUE_BYTES) {
        scan_result_t result;
        memset(&result, 0, sizeof(result));
        result.size = (int)len64;
        result.type = kind;
        memcpy(result.bytes, buf, (size_t)len64);
        result_value_text(&result, value, sizeof(value));
    }
    send_header(client, "200 OK", "application/json");
    char tmp[384];
    snprintf(tmp, sizeof(tmp), "{\"pid\":%d,\"addr\":\"0x%llX\",\"hex\":\"", eboot.pid, (unsigned long long)addr);
    write_all(client, tmp);
    write_all(client, hex);
    write_all(client, "\",\"value\":");
    json_string(client, value);
    write_all(client, "}");
    free(buf);
    free(hex);
}

static void send_write(int client, const char *query)
{
    eboot_info_t eboot;
    char addr_text[64], hex_text[256], value_text[64], type_text[16];
    uint64_t addr = 0;
    uint8_t bytes[64];
    int len = 0;

    if (!find_eboot(&eboot)) {
        send_response(client, "404 Not Found", "application/json", "{\"error\":\"eboot_not_found\"}");
        return;
    }
    if (!query_get(query, "addr", addr_text, sizeof(addr_text)) || !parse_u64(addr_text, &addr)) {
        send_response(client, "400 Bad Request", "application/json", "{\"error\":\"bad_addr\"}");
        return;
    }

    if (query_get(query, "hex", hex_text, sizeof(hex_text))) {
        if (!parse_hex_bytes(hex_text, bytes, &len, (int)sizeof(bytes))) {
            send_response(client, "400 Bad Request", "application/json", "{\"error\":\"bad_hex\"}");
            return;
        }
    } else if (query_get(query, "value", value_text, sizeof(value_text))) {
        value_kind_t kind = VALUE_U32;
        query_get(query, "type", type_text, sizeof(type_text));
        if (!parse_value_kind(type_text, &kind) || !encode_value(kind, value_text, bytes, &len)) {
            send_response(client, "400 Bad Request", "application/json", "{\"error\":\"bad_value_or_type\"}");
            return;
        }
    } else {
        send_response(client, "400 Bad Request", "application/json", "{\"error\":\"missing_hex_or_value\"}");
        return;
    }

    int old_prot = kernel_get_vmem_protection(eboot.pid, (intptr_t)addr, (size_t)len);
    int protect_rc = 0;
    int restore_rc = 0;
    if (old_prot >= 0) {
        protect_rc = kernel_set_vmem_protection(eboot.pid, (intptr_t)addr, (size_t)len, old_prot | PROT_READ | PROT_WRITE);
    }

    int ptrace_rc = 0;
    int rc = mdbg_copyin(eboot.pid, bytes, (intptr_t)addr, (size_t)len);
    if (rc) {
        ptrace_rc = ptrace_write_memory(eboot.pid, addr, bytes, (size_t)len);
        if (!ptrace_rc) {
            rc = 0;
        }
    }
    g_last_errno = rc ? errno : 0;
    if (old_prot >= 0) {
        restore_rc = kernel_set_vmem_protection(eboot.pid, (intptr_t)addr, (size_t)len, old_prot);
    }

    char body[256];
    snprintf(body, sizeof(body),
        "{\"ok\":%s,\"pid\":%d,\"addr\":\"0x%llX\",\"length\":%d,\"oldProt\":%d,"
        "\"protectRc\":%d,\"writeRc\":%d,\"ptraceRc\":%d,\"restoreRc\":%d,\"lastErrno\":%d}",
        rc ? "false" : "true",
        eboot.pid,
        (unsigned long long)addr,
        len,
        old_prot,
        protect_rc,
        rc,
        ptrace_rc,
        restore_rc,
        g_last_errno);
    send_response(client, rc ? "500 Internal Server Error" : "200 OK", "application/json", body);
}

typedef struct scan_ctx {
    pid_t pid;
    const uint8_t *needle;
    int size;
    int step;
    const char *filter;
    uint64_t scanned_bytes;
    bool capped;
} scan_ctx_t;

static bool add_scan_result(const struct kinfo_vmentry *entry, uint64_t addr, const uint8_t *bytes, int size)
{
    if (!g_results) {
        g_results = calloc(MAX_SCAN_RESULTS, sizeof(scan_result_t));
    }
    if (!g_results || g_result_count >= MAX_SCAN_RESULTS) {
        return false;
    }

    scan_result_t *r = &g_results[g_result_count++];
    char name[96];
    section_name(entry, name, sizeof(name));
    r->address = addr;
    r->section_start = entry->kve_start;
    r->size = size;
    memcpy(r->bytes, bytes, (size_t)size);
    snprintf(r->section, sizeof(r->section), "%s", name);
    return true;
}

static bool scan_entry_cb(const struct kinfo_vmentry *entry, void *ctx_ptr)
{
    scan_ctx_t *ctx = (scan_ctx_t *)ctx_ptr;
    uint64_t length;
    uint8_t *buf;

    if ((entry->kve_protection & KVME_PROT_READ) != KVME_PROT_READ || entry->kve_end <= entry->kve_start) {
        return true;
    }
    if (!section_matches_filter(entry, ctx->filter)) {
        return true;
    }

    length = entry->kve_end - entry->kve_start;
    if (length > MAX_SECTION_SCAN_BYTES) {
        length = MAX_SECTION_SCAN_BYTES;
    }

    buf = malloc(SCAN_CHUNK_SIZE + 8);
    if (!buf) {
        ctx->capped = true;
        return false;
    }

    for (uint64_t offset = 0; offset < length; offset += SCAN_CHUNK_SIZE) {
        uint64_t remaining = length - offset;
        size_t to_read = (size_t)(remaining > SCAN_CHUNK_SIZE ? SCAN_CHUNK_SIZE : remaining);
        uint64_t base = entry->kve_start + offset;

        if (mdbg_copyout(ctx->pid, (intptr_t)base, buf, to_read)) {
            g_last_errno = errno;
            continue;
        }

        for (size_t i = 0; i + (size_t)ctx->size <= to_read; i += (size_t)ctx->step) {
            if (!memcmp(buf + i, ctx->needle, (size_t)ctx->size)) {
                if (!add_scan_result(entry, base + i, buf + i, ctx->size)) {
                    ctx->capped = true;
                    free(buf);
                    return false;
                }
            }
        }
        ctx->scanned_bytes += to_read;
    }

    free(buf);
    return true;
}

static void send_results_payload(int client, const char *prefix, uint64_t scanned_bytes, bool capped)
{
    char tmp[256], hex[32];
    size_t preview = g_result_count < 500 ? g_result_count : 500;
    send_header(client, "200 OK", "application/json");
    snprintf(tmp, sizeof(tmp), "%s\"count\":%llu,\"shown\":%llu,\"capped\":%s,\"scannedBytes\":%llu,\"results\":[",
        prefix,
        (unsigned long long)g_result_count,
        (unsigned long long)preview,
        capped ? "true" : "false",
        (unsigned long long)scanned_bytes);
    write_all(client, tmp);

    for (size_t i = 0; i < preview; i++) {
        scan_result_t *r = &g_results[i];
        bytes_to_hex(r->bytes, r->size, hex, sizeof(hex));
        if (i) write_all(client, ",");
        snprintf(tmp, sizeof(tmp), "{\"address\":\"0x%llX\",\"value\":%llu,\"hex\":\"%s\",\"section\":",
            (unsigned long long)r->address,
            (unsigned long long)bytes_to_value(r->bytes, r->size),
            hex);
        write_all(client, tmp);
        json_string(client, r->section);
        write_all(client, "}");
    }

    write_all(client, "]}");
}

static void send_scan(int client, const char *query)
{
    eboot_info_t eboot;
    char value_text[64], type_text[16], aligned_text[16], filter_text[128];
    uint64_t value = 0;
    int type = 4;
    int aligned = 1;
    uint8_t needle[8];
    scan_ctx_t ctx;

    if (!find_eboot(&eboot)) {
        send_response(client, "404 Not Found", "application/json", "{\"error\":\"eboot_not_found\"}");
        return;
    }
    if (!query_get(query, "value", value_text, sizeof(value_text)) || !parse_u64(value_text, &value)) {
        send_response(client, "400 Bad Request", "application/json", "{\"error\":\"missing_or_bad_value\"}");
        return;
    }
    if (query_get(query, "type", type_text, sizeof(type_text))) {
        type = atoi(type_text);
    }
    if (query_get(query, "aligned", aligned_text, sizeof(aligned_text))) {
        aligned = atoi(aligned_text);
    }
    query_get(query, "filter", filter_text, sizeof(filter_text));

    if (!(type == 1 || type == 2 || type == 4 || type == 8)) {
        send_response(client, "400 Bad Request", "application/json", "{\"error\":\"type_must_be_1_2_4_8\"}");
        return;
    }

    value_to_bytes(value, type, needle);
    g_result_count = 0;
    g_result_size = type;
    memset(&ctx, 0, sizeof(ctx));
    ctx.pid = eboot.pid;
    ctx.needle = needle;
    ctx.size = type;
    ctx.step = aligned ? type : 1;
    ctx.filter = filter_text;

    each_vm_entry(eboot.pid, scan_entry_cb, &ctx);
    send_results_payload(client, "{\"mode\":\"first\",", ctx.scanned_bytes, ctx.capped);
}

static void send_next(int client, const char *query)
{
    eboot_info_t eboot;
    char value_text[64], hex[32], section[96];
    uint64_t value = 0;
    uint8_t needle[8], current[8];
    size_t write_index = 0;

    if (!find_eboot(&eboot)) {
        send_response(client, "404 Not Found", "application/json", "{\"error\":\"eboot_not_found\"}");
        return;
    }
    if (!g_results || g_result_count == 0) {
        send_response(client, "400 Bad Request", "application/json", "{\"error\":\"run_first_scan_before_next\"}");
        return;
    }
    if (!query_get(query, "value", value_text, sizeof(value_text)) || !parse_u64(value_text, &value)) {
        send_response(client, "400 Bad Request", "application/json", "{\"error\":\"missing_or_bad_value\"}");
        return;
    }

    value_to_bytes(value, g_result_size, needle);
    size_t old_count = g_result_count;
    for (size_t i = 0; i < old_count; i++) {
        scan_result_t *r = &g_results[i];
        if (mdbg_copyout(eboot.pid, (intptr_t)r->address, current, (size_t)g_result_size)) {
            g_last_errno = errno;
            continue;
        }
        if (!memcmp(current, needle, (size_t)g_result_size)) {
            bytes_to_hex(current, g_result_size, hex, sizeof(hex));
            snprintf(section, sizeof(section), "%s", r->section);
            g_results[write_index] = *r;
            memcpy(g_results[write_index].bytes, current, (size_t)g_result_size);
            snprintf(g_results[write_index].section, sizeof(g_results[write_index].section), "%s", section);
            write_index++;
        }
    }
    g_result_count = write_index;
    send_results_payload(client, "{\"mode\":\"next\",", old_count, false);
}

static void send_results(int client)
{
    send_results_payload(client, "{\"mode\":\"cached\",", 0, false);
}

static uint64_t monotonic_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000ULL + (uint64_t)ts.tv_nsec / 1000000ULL;
}

static const char *scan_state_name(scan_state_t state)
{
    switch (state) {
        case SCAN_RUNNING: return "running";
        case SCAN_COMPLETED: return "completed";
        case SCAN_CANCELLED: return "cancelled";
        case SCAN_ERROR: return "error";
        default: return "idle";
    }
}

static const char *value_kind_name(value_kind_t kind)
{
    switch (kind) {
        case VALUE_U8: return "1";
        case VALUE_U16: return "2";
        case VALUE_U64: return "8";
        case VALUE_FLOAT: return "float";
        case VALUE_DOUBLE: return "double";
        case VALUE_HEX: return "hex";
        case VALUE_STRING: return "string";
        default: return "4";
    }
}

static bool parse_value_kind(const char *text, value_kind_t *kind)
{
    if (!text || !text[0] || !strcmp(text, "4") || !strcmp(text, "u32")) *kind = VALUE_U32;
    else if (!strcmp(text, "1") || !strcmp(text, "u8")) *kind = VALUE_U8;
    else if (!strcmp(text, "2") || !strcmp(text, "u16")) *kind = VALUE_U16;
    else if (!strcmp(text, "8") || !strcmp(text, "u64") || !strcmp(text, "pointer")) *kind = VALUE_U64;
    else if (!strcmp(text, "float")) *kind = VALUE_FLOAT;
    else if (!strcmp(text, "double")) *kind = VALUE_DOUBLE;
    else if (!strcmp(text, "hex")) *kind = VALUE_HEX;
    else if (!strcmp(text, "string")) *kind = VALUE_STRING;
    else return false;
    return true;
}

static compare_kind_t parse_compare_kind(const char *text)
{
    if (!text || !strcmp(text, "exact")) return COMPARE_EXACT;
    if (!strcmp(text, "fuzzy")) return COMPARE_FUZZY;
    if (!strcmp(text, "increased")) return COMPARE_INCREASED;
    if (!strcmp(text, "increasedby")) return COMPARE_INCREASED_BY;
    if (!strcmp(text, "decreased")) return COMPARE_DECREASED;
    if (!strcmp(text, "decreasedby")) return COMPARE_DECREASED_BY;
    if (!strcmp(text, "bigger")) return COMPARE_BIGGER;
    if (!strcmp(text, "smaller")) return COMPARE_SMALLER;
    if (!strcmp(text, "changed")) return COMPARE_CHANGED;
    if (!strcmp(text, "unchanged")) return COMPARE_UNCHANGED;
    if (!strcmp(text, "between")) return COMPARE_BETWEEN;
    if (!strcmp(text, "unknown")) return COMPARE_UNKNOWN;
    return COMPARE_EXACT;
}

static int fixed_value_size(value_kind_t kind)
{
    switch (kind) {
        case VALUE_U8: return 1;
        case VALUE_U16: return 2;
        case VALUE_U64: return 8;
        case VALUE_FLOAT: return 4;
        case VALUE_DOUBLE: return 8;
        case VALUE_U32: return 4;
        default: return 0;
    }
}

static bool encode_value(value_kind_t kind, const char *text, uint8_t out[MAX_VALUE_BYTES], int *size)
{
    memset(out, 0, MAX_VALUE_BYTES);
    int fixed = fixed_value_size(kind);
    if (fixed > 0) {
        if (kind == VALUE_FLOAT) {
            char *end = NULL;
            errno = 0;
            float value = strtof(text ? text : "", &end);
            if (errno || end == text || *end) return false;
            memcpy(out, &value, sizeof(value));
        } else if (kind == VALUE_DOUBLE) {
            char *end = NULL;
            errno = 0;
            double value = strtod(text ? text : "", &end);
            if (errno || end == text || *end) return false;
            memcpy(out, &value, sizeof(value));
        } else {
            uint64_t value = 0;
            if (!parse_u64(text, &value)) return false;
            value_to_bytes(value, fixed, out);
        }
        *size = fixed;
        return true;
    }

    if (kind == VALUE_HEX) {
        int parsed = 0;
        if (!parse_hex_bytes(text ? text : "", out, &parsed, MAX_VALUE_BYTES)) return false;
        *size = parsed;
        return true;
    }
    if (kind == VALUE_STRING) {
        size_t length = strlen(text ? text : "");
        if (length == 0 || length > MAX_VALUE_BYTES) return false;
        memcpy(out, text, length);
        *size = (int)length;
        return true;
    }
    return false;
}

static bool build_value_spec(const char *query, value_spec_t *spec, char *error, size_t error_size)
{
    char type_text[24] = "4", compare_text[32] = "exact", value_text[128] = "", second_text[128] = "";
    memset(spec, 0, sizeof(*spec));
    query_get(query, "type", type_text, sizeof(type_text));
    query_get(query, "compare", compare_text, sizeof(compare_text));
    query_get(query, "value", value_text, sizeof(value_text));
    query_get(query, "second", second_text, sizeof(second_text));

    if (!parse_value_kind(type_text, &spec->kind)) {
        snprintf(error, error_size, "unsupported_value_type");
        return false;
    }
    spec->compare = parse_compare_kind(compare_text);
    spec->size = fixed_value_size(spec->kind);

    bool no_value = spec->compare == COMPARE_UNKNOWN || spec->compare == COMPARE_CHANGED || spec->compare == COMPARE_UNCHANGED;
    if (!no_value && !encode_value(spec->kind, value_text, spec->first, &spec->size)) {
        snprintf(error, error_size, "missing_or_bad_value");
        return false;
    }
    if (spec->compare == COMPARE_BETWEEN) {
        int second_size = 0;
        if (!encode_value(spec->kind, second_text, spec->second, &second_size) || second_size != spec->size) {
            snprintf(error, error_size, "missing_or_bad_second_value");
            return false;
        }
    }
    if (spec->size <= 0 || spec->size > MAX_VALUE_BYTES) {
        snprintf(error, error_size, "value_size_out_of_range");
        return false;
    }
    return true;
}

static double value_as_number(value_kind_t kind, const uint8_t *bytes)
{
    switch (kind) {
        case VALUE_U8: return bytes[0];
        case VALUE_U16: { uint16_t value; memcpy(&value, bytes, sizeof(value)); return value; }
        case VALUE_U32: { uint32_t value; memcpy(&value, bytes, sizeof(value)); return value; }
        case VALUE_U64: { uint64_t value; memcpy(&value, bytes, sizeof(value)); return (double)value; }
        case VALUE_FLOAT: { float value; memcpy(&value, bytes, sizeof(value)); return value; }
        case VALUE_DOUBLE: { double value; memcpy(&value, bytes, sizeof(value)); return value; }
        default: return 0;
    }
}

static bool value_matches(const value_spec_t *spec, const uint8_t *old_value, const uint8_t *current)
{
    if (spec->compare == COMPARE_UNKNOWN) return true;
    if (spec->compare == COMPARE_CHANGED) return memcmp(current, old_value, (size_t)spec->size) != 0;
    if (spec->compare == COMPARE_UNCHANGED) return memcmp(current, old_value, (size_t)spec->size) == 0;
    if (spec->kind == VALUE_HEX || spec->kind == VALUE_STRING) {
        return spec->compare == COMPARE_EXACT && memcmp(current, spec->first, (size_t)spec->size) == 0;
    }

    double current_value = value_as_number(spec->kind, current);
    double first = value_as_number(spec->kind, spec->first);
    double second = value_as_number(spec->kind, spec->second);
    double old = old_value ? value_as_number(spec->kind, old_value) : current_value;
    double delta = current_value - first;
    if (delta < 0) delta = -delta;
    double tolerance = first < 0 ? -first : first;
    tolerance = tolerance * 0.01;
    if (tolerance < 1.0) tolerance = 1.0;

    switch (spec->compare) {
        case COMPARE_EXACT: return current_value == first;
        case COMPARE_FUZZY: return delta <= tolerance;
        case COMPARE_INCREASED: return current_value > old;
        case COMPARE_INCREASED_BY: return (current_value - old) == first;
        case COMPARE_DECREASED: return current_value < old;
        case COMPARE_DECREASED_BY: return (old - current_value) == first;
        case COMPARE_BIGGER: return current_value > first;
        case COMPARE_SMALLER: return current_value < first;
        case COMPARE_BETWEEN: return current_value >= (first < second ? first : second) && current_value <= (first > second ? first : second);
        default: return false;
    }
}

static void result_value_text(const scan_result_t *result, char *out, size_t out_size)
{
    switch ((value_kind_t)result->type) {
        case VALUE_FLOAT: { float value; memcpy(&value, result->bytes, sizeof(value)); snprintf(out, out_size, "%.9g", value); break; }
        case VALUE_DOUBLE: { double value; memcpy(&value, result->bytes, sizeof(value)); snprintf(out, out_size, "%.17g", value); break; }
        case VALUE_STRING: {
            size_t length = (size_t)result->size < out_size - 1 ? (size_t)result->size : out_size - 1;
            memcpy(out, result->bytes, length); out[length] = 0;
            for (size_t i = 0; i < length; i++) if ((unsigned char)out[i] < 0x20) out[i] = ' ';
            break;
        }
        case VALUE_HEX: bytes_to_hex(result->bytes, result->size, out, out_size); break;
        default: snprintf(out, out_size, "%llu", (unsigned long long)bytes_to_value(result->bytes, result->size)); break;
    }
}

static int pause_process(pid_t pid)
{
    pthread_mutex_lock(&g_state_lock);
    if (g_paused_pid == pid) {
        pthread_mutex_unlock(&g_state_lock);
        return 0;
    }
    pthread_mutex_unlock(&g_state_lock);
    if (privileged_ptrace(PT_ATTACH, pid, 0, 0)) return -1;
    waitpid(pid, 0, 0);
    pthread_mutex_lock(&g_state_lock);
    g_paused_pid = pid;
    pthread_mutex_unlock(&g_state_lock);
    return 0;
}

static int resume_process(void)
{
    pthread_mutex_lock(&g_state_lock);
    pid_t pid = g_paused_pid;
    pthread_mutex_unlock(&g_state_lock);
    if (pid < 0) return 0;
    int rc = privileged_ptrace(PT_DETACH, pid, 0, 0);
    if (!rc) {
        pthread_mutex_lock(&g_state_lock);
        g_paused_pid = -1;
        pthread_mutex_unlock(&g_state_lock);
    }
    return rc;
}

static bool append_local_result(scan_result_t *results, size_t *count, const memory_section_t *section,
    uint64_t address, const uint8_t *bytes, const value_spec_t *spec)
{
    if (*count >= MAX_SCAN_RESULTS) return false;
    scan_result_t *result = &results[(*count)++];
    memset(result, 0, sizeof(*result));
    result->address = address;
    result->section_start = section ? section->start : 0;
    result->size = spec->size;
    result->type = spec->kind;
    memcpy(result->bytes, bytes, (size_t)spec->size);
    snprintf(result->section, sizeof(result->section), "%s", section ? section->display : "Memory");
    return true;
}

static bool scan_cancelled(void)
{
    pthread_mutex_lock(&g_state_lock);
    bool cancelled = g_scan.cancel || !g_running;
    pthread_mutex_unlock(&g_state_lock);
    return cancelled;
}

static void update_scan_progress(uint64_t completed, size_t matches, const char *message)
{
    pthread_mutex_lock(&g_state_lock);
    g_scan.completed_units = completed;
    g_scan.matches = matches;
    snprintf(g_scan.message, sizeof(g_scan.message), "%s", message);
    pthread_mutex_unlock(&g_state_lock);
}

static void finish_scan(scan_task_t *task, scan_result_t *new_results, size_t new_count, scan_state_t state, const char *error, bool capped)
{
    if (task->auto_pause) resume_process();
    pthread_mutex_lock(&g_state_lock);
    if (state == SCAN_COMPLETED) {
        free(g_results);
        g_results = new_results;
        g_result_count = new_count;
        g_result_size = task->spec.size;
        g_result_type = task->spec.kind;
        new_results = NULL;
    }
    g_scan.state = state;
    g_scan.matches = state == SCAN_COMPLETED ? new_count : g_scan.matches;
    g_scan.capped = capped;
    g_scan.finished_ms = monotonic_ms();
    snprintf(g_scan.message, sizeof(g_scan.message), "%s", state == SCAN_COMPLETED ? "Scan completed" : (state == SCAN_CANCELLED ? "Scan cancelled" : "Scan failed"));
    snprintf(g_scan.error, sizeof(g_scan.error), "%s", error ? error : "");
    pthread_mutex_unlock(&g_state_lock);
    free(new_results);
    free(task->sections);
    free(task->previous);
    free(task);
}

static void *scan_worker(void *arg)
{
    scan_task_t *task = (scan_task_t *)arg;
    scan_result_t *matches = calloc(MAX_SCAN_RESULTS, sizeof(scan_result_t));
    size_t match_count = 0;
    uint64_t completed = 0;
    bool capped = false;

    if (!matches) {
        finish_scan(task, NULL, 0, SCAN_ERROR, "out_of_memory", false);
        return NULL;
    }
    if (task->auto_pause && pause_process(task->pid)) {
        finish_scan(task, matches, 0, SCAN_ERROR, "auto_pause_failed", false);
        return NULL;
    }

    if (task->mode == 1) {
        uint8_t *buffer = malloc(SCAN_CHUNK_SIZE + MAX_VALUE_BYTES);
        if (!buffer) {
            finish_scan(task, matches, 0, SCAN_ERROR, "out_of_memory", false);
            return NULL;
        }
        int step = task->aligned ? task->spec.size : 1;
        for (size_t section_index = 0; section_index < task->section_count && !capped; section_index++) {
            memory_section_t *section = &task->sections[section_index];
            uint64_t length = section->end - section->start;
            if (length > MAX_SECTION_SCAN_BYTES) length = MAX_SECTION_SCAN_BYTES;
            for (uint64_t offset = 0; offset < length; offset += SCAN_CHUNK_SIZE) {
                if (scan_cancelled()) { free(buffer); finish_scan(task, matches, match_count, SCAN_CANCELLED, NULL, false); return NULL; }
                uint64_t core = length - offset;
                if (core > SCAN_CHUNK_SIZE) core = SCAN_CHUNK_SIZE;
                uint64_t available = length - offset;
                size_t read_size = (size_t)core;
                if (available > core) {
                    uint64_t overlap = available - core;
                    if (overlap > (uint64_t)(task->spec.size - 1)) overlap = (uint64_t)(task->spec.size - 1);
                    read_size += (size_t)overlap;
                }
                uint64_t base = section->start + offset;
                if (!mdbg_copyout(task->pid, (intptr_t)base, buffer, read_size)) {
                    for (size_t i = 0; i < (size_t)core && i + (size_t)task->spec.size <= read_size; i += (size_t)step) {
                        if (value_matches(&task->spec, NULL, buffer + i) &&
                            !append_local_result(matches, &match_count, section, base + i, buffer + i, &task->spec)) {
                            capped = true;
                            break;
                        }
                    }
                } else {
                    g_last_errno = errno;
                }
                completed += core;
                update_scan_progress(completed, match_count, section->display);
            }
        }
        free(buffer);
    } else {
        uint8_t current[MAX_VALUE_BYTES];
        for (size_t i = 0; i < task->previous_count; i++) {
            if ((i & 127U) == 0 && scan_cancelled()) { finish_scan(task, matches, match_count, SCAN_CANCELLED, NULL, false); return NULL; }
            scan_result_t *previous = &task->previous[i];
            if (!mdbg_copyout(task->pid, (intptr_t)previous->address, current, (size_t)task->spec.size) &&
                value_matches(&task->spec, previous->bytes, current)) {
                memory_section_t fake;
                memset(&fake, 0, sizeof(fake));
                fake.start = previous->section_start;
                snprintf(fake.display, sizeof(fake.display), "%s", previous->section);
                if (!append_local_result(matches, &match_count, &fake, previous->address, current, &task->spec)) {
                    capped = true;
                    break;
                }
            }
            completed++;
            if ((i & 127U) == 0 || i + 1 == task->previous_count) update_scan_progress(completed, match_count, "Analyzing cached addresses");
        }
    }

    finish_scan(task, matches, match_count, SCAN_COMPLETED, NULL, capped);
    return NULL;
}

static void send_scan_start(int client, const char *query, int mode)
{
    eboot_info_t eboot;
    scan_task_t *task = NULL;
    char error[128] = "";
    char aligned_text[8] = "1", autopause_text[8] = "0";

    if (!find_eboot(&eboot)) {
        send_response(client, "404 Not Found", "application/json", "{\"error\":\"eboot_not_found\",\"message\":\"Start a game before scanning.\"}");
        return;
    }

    pthread_mutex_lock(&g_state_lock);
    bool running = g_scan.state == SCAN_RUNNING;
    pthread_mutex_unlock(&g_state_lock);
    if (running) {
        send_response(client, "409 Conflict", "application/json", "{\"error\":\"scan_already_running\"}");
        return;
    }
    if (g_section_pid != eboot.pid || g_section_count == 0) load_memory_sections(eboot.pid);

    task = calloc(1, sizeof(scan_task_t));
    if (!task || !build_value_spec(query, &task->spec, error, sizeof(error))) {
        free(task);
        char body[256];
        snprintf(body, sizeof(body), "{\"error\":\"%s\",\"message\":\"Please input a valid scan value.\"}", error[0] ? error : "out_of_memory");
        send_response(client, "400 Bad Request", "application/json", body);
        return;
    }
    query_get(query, "aligned", aligned_text, sizeof(aligned_text));
    query_get(query, "autopause", autopause_text, sizeof(autopause_text));
    task->mode = mode;
    task->pid = eboot.pid;
    task->aligned = atoi(aligned_text) != 0;
    task->auto_pause = atoi(autopause_text) != 0;

    pthread_mutex_lock(&g_state_lock);
    uint64_t total = 0;
    if (mode == 1) {
        size_t selected = 0;
        for (size_t i = 0; i < g_section_count; i++) if (g_sections[i].selected) selected++;
        if (selected > 0) task->sections = calloc(selected, sizeof(memory_section_t));
        for (size_t i = 0, write_index = 0; i < g_section_count; i++) {
            if (!g_sections[i].selected) continue;
            task->sections[write_index++] = g_sections[i];
            uint64_t length = g_sections[i].end - g_sections[i].start;
            total += length > MAX_SECTION_SCAN_BYTES ? MAX_SECTION_SCAN_BYTES : length;
        }
        task->section_count = selected;
    } else {
        if (!g_results || g_result_count == 0) {
            pthread_mutex_unlock(&g_state_lock);
            free(task);
            send_response(client, "400 Bad Request", "application/json", "{\"error\":\"run_first_scan_before_next\"}");
            return;
        }
        if ((int)task->spec.kind != g_result_type || task->spec.size != g_result_size) {
            pthread_mutex_unlock(&g_state_lock);
            free(task);
            send_response(client, "400 Bad Request", "application/json", "{\"error\":\"value_type_changed\",\"message\":\"Keep the same value type for Next Scan.\"}");
            return;
        }
        task->previous_count = g_result_count;
        task->previous = malloc(g_result_count * sizeof(scan_result_t));
        if (task->previous) memcpy(task->previous, g_results, g_result_count * sizeof(scan_result_t));
        total = g_result_count;
    }

    if ((mode == 1 && (!task->sections || task->section_count == 0)) || (mode == 2 && !task->previous)) {
        pthread_mutex_unlock(&g_state_lock);
        free(task->sections); free(task->previous); free(task);
        send_response(client, "400 Bad Request", "application/json", "{\"error\":\"no_memory_selected_or_oom\"}");
        return;
    }

    memset(&g_scan, 0, sizeof(g_scan));
    g_scan.state = SCAN_RUNNING;
    g_scan.mode = mode;
    g_scan.pid = eboot.pid;
    g_scan.auto_pause = task->auto_pause;
    g_scan.total_units = total;
    g_scan.started_ms = monotonic_ms();
    snprintf(g_scan.message, sizeof(g_scan.message), "%s", mode == 1 ? "Starting first scan" : "Starting next scan");
    pthread_mutex_unlock(&g_state_lock);

    pthread_t thread;
    int rc = pthread_create(&thread, NULL, scan_worker, task);
    if (rc != 0) {
        pthread_mutex_lock(&g_state_lock);
        g_scan.state = SCAN_ERROR;
        snprintf(g_scan.error, sizeof(g_scan.error), "pthread_create_%d", rc);
        pthread_mutex_unlock(&g_state_lock);
        free(task->sections); free(task->previous); free(task);
        send_response(client, "500 Internal Server Error", "application/json", "{\"error\":\"scan_thread_failed\"}");
        return;
    }
    pthread_detach(thread);
    send_response(client, "202 Accepted", "application/json", mode == 1 ? "{\"started\":true,\"mode\":\"first\"}" : "{\"started\":true,\"mode\":\"next\"}");
}

static void send_scan_progress(int client)
{
    scan_job_t snapshot;
    pthread_mutex_lock(&g_state_lock);
    snapshot = g_scan;
    pthread_mutex_unlock(&g_state_lock);
    double percent = snapshot.total_units ? ((double)snapshot.completed_units * 100.0 / (double)snapshot.total_units) : 0.0;
    if (snapshot.state == SCAN_COMPLETED) percent = 100.0;
    if (percent > 100.0) percent = 100.0;
    char body[640];
    snprintf(body, sizeof(body),
        "{\"state\":\"%s\",\"mode\":\"%s\",\"percent\":%.2f,\"completed\":%llu,\"total\":%llu,\"matches\":%llu,\"capped\":%s,\"elapsedMs\":%llu,\"message\":\"%s\",\"error\":\"%s\"}",
        scan_state_name(snapshot.state), snapshot.mode == 2 ? "next" : "first", percent,
        (unsigned long long)snapshot.completed_units, (unsigned long long)snapshot.total_units,
        (unsigned long long)snapshot.matches, snapshot.capped ? "true" : "false",
        (unsigned long long)((snapshot.finished_ms ? snapshot.finished_ms : monotonic_ms()) - snapshot.started_ms),
        snapshot.message, snapshot.error);
    send_response(client, "200 OK", "application/json", body);
}

static void send_scan_stop(int client)
{
    pthread_mutex_lock(&g_state_lock);
    bool running = g_scan.state == SCAN_RUNNING;
    if (running) g_scan.cancel = true;
    pthread_mutex_unlock(&g_state_lock);
    send_response(client, "200 OK", "application/json", running ? "{\"stopping\":true}" : "{\"stopping\":false}");
}

static void send_results_safe(int client)
{
    scan_result_t *snapshot = NULL;
    size_t count = 0, preview = 0;
    bool capped = false;
    pthread_mutex_lock(&g_state_lock);
    count = g_result_count;
    preview = count < 500 ? count : 500;
    capped = g_scan.capped;
    if (preview) {
        snapshot = malloc(preview * sizeof(scan_result_t));
        if (snapshot) memcpy(snapshot, g_results, preview * sizeof(scan_result_t));
    }
    pthread_mutex_unlock(&g_state_lock);

    if (preview && !snapshot) {
        send_response(client, "500 Internal Server Error", "application/json", "{\"error\":\"out_of_memory\"}");
        return;
    }
    send_header(client, "200 OK", "application/json");
    char tmp[512], hex[2 * MAX_VALUE_BYTES + 1], value[160];
    snprintf(tmp, sizeof(tmp), "{\"count\":%llu,\"shown\":%llu,\"capped\":%s,\"results\":[",
        (unsigned long long)count, (unsigned long long)preview, capped ? "true" : "false");
    write_all(client, tmp);
    for (size_t i = 0; i < preview; i++) {
        scan_result_t *result = &snapshot[i];
        bytes_to_hex(result->bytes, result->size, hex, sizeof(hex));
        result_value_text(result, value, sizeof(value));
        if (i) write_all(client, ",");
        snprintf(tmp, sizeof(tmp), "{\"address\":\"0x%llX\",\"sectionStart\":\"0x%llX\",\"type\":",
            (unsigned long long)result->address, (unsigned long long)result->section_start);
        write_all(client, tmp);
        json_string(client, value_kind_name((value_kind_t)result->type));
        write_all(client, ",\"value\":"); json_string(client, value);
        write_all(client, ",\"hex\":"); json_string(client, hex);
        write_all(client, ",\"section\":"); json_string(client, result->section);
        write_all(client, "}");
    }
    write_all(client, "]}");
    free(snapshot);
}

static void send_process_action(int client, const char *action)
{
    eboot_info_t eboot;
    if (!strcmp(action, "resume")) {
        int rc = resume_process();
        send_response(client, rc ? "500 Internal Server Error" : "200 OK", "application/json",
            rc ? "{\"error\":\"resume_failed\"}" : "{\"ok\":true,\"message\":\"Process resumed.\"}");
        return;
    }
    if (!find_eboot(&eboot)) {
        send_response(client, "404 Not Found", "application/json", "{\"error\":\"eboot_not_found\"}");
        return;
    }
    pthread_mutex_lock(&g_state_lock);
    bool scanning = g_scan.state == SCAN_RUNNING;
    pthread_mutex_unlock(&g_state_lock);
    if (scanning) {
        send_response(client, "409 Conflict", "application/json", "{\"error\":\"stop_scan_first\"}");
        return;
    }
    if (!strcmp(action, "pause")) {
        int rc = pause_process(eboot.pid);
        send_response(client, rc ? "500 Internal Server Error" : "200 OK", "application/json",
            rc ? "{\"error\":\"pause_failed\"}" : "{\"ok\":true,\"message\":\"Process paused.\"}");
        return;
    }
    if (!strcmp(action, "kill")) {
        if (g_paused_pid != eboot.pid && pause_process(eboot.pid)) {
            send_response(client, "500 Internal Server Error", "application/json", "{\"error\":\"attach_before_kill_failed\"}");
            return;
        }
        int rc = privileged_ptrace(PT_KILL, eboot.pid, 0, 0);
        pthread_mutex_lock(&g_state_lock);
        g_paused_pid = -1;
        g_section_pid = -1;
        g_section_count = 0;
        free(g_results); g_results = NULL; g_result_count = 0;
        pthread_mutex_unlock(&g_state_lock);
        send_response(client, rc ? "500 Internal Server Error" : "200 OK", "application/json",
            rc ? "{\"error\":\"kill_failed\"}" : "{\"ok\":true,\"message\":\"EBOOT killed. App state reset.\"}");
        return;
    }
    send_response(client, "404 Not Found", "application/json", "{\"error\":\"unknown_process_action\"}");
}

static void send_legacy_index(int client)
{
    send_response(client, "200 OK", "text/html; charset=utf-8",
        "<!doctype html><html><head><meta charset=\"utf-8\">"
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
        "<title>PS5MemoryPeeker Web</title>"
        "<style>"
        "body{margin:0;background:#070a0f;color:#edf4ff;font:15px Segoe UI,Arial,sans-serif}"
        "main{max-width:1180px;margin:auto;padding:22px}"
        "h1{font-size:24px;margin:0 0 14px}.grid{display:grid;grid-template-columns:1fr 1fr;gap:14px}"
        ".panel{border:1px solid #263244;background:#0b111a;padding:14px}.row{display:flex;gap:8px;flex-wrap:wrap;margin:8px 0}"
        "input,select,button{background:#111a27;color:#edf4ff;border:1px solid #34445b;padding:10px;min-height:40px}"
        "button{background:#1677ff;border-color:#1677ff;cursor:pointer}.danger{background:#1a1111;border-color:#6b2323}"
        "pre{white-space:pre-wrap;max-height:460px;overflow:auto;background:#05070b;border:1px solid #263244;padding:12px}"
        "@media(max-width:900px){.grid{grid-template-columns:1fr}}"
        "</style></head><body><main>"
        "<h1>PS5MemoryPeeker Web</h1>"
        "<div class=\"grid\"><section class=\"panel\"><div class=\"row\">"
        "<button onclick=\"api('/api/eboot')\">EBOOT</button><button onclick=\"api('/api/maps')\">Memory Sections</button>"
        "<button onclick=\"api('/api/status')\">Status</button></div>"
        "<div class=\"row\"><input id=\"value\" placeholder=\"Value\" inputmode=\"numeric\"><select id=\"type\"><option value=\"4\">4 bytes</option><option value=\"1\">1 byte</option><option value=\"2\">2 bytes</option><option value=\"8\">8 bytes</option></select>"
        "<input id=\"filter\" placeholder=\"Filter e.g. executable, anon, app0\"><button onclick=\"scan()\">First Scan</button><button onclick=\"nextScan()\">Next Scan</button></div>"
        "<div class=\"row\"><input id=\"addr\" placeholder=\"Address 0x...\"/><input id=\"writeInput\" placeholder=\"Write value\"/><button onclick=\"writeAddr()\">Write</button><button onclick=\"readAddr()\">Read 16</button></div>"
        "<div class=\"row\"><button class=\"danger\" onclick=\"api('/api/stop')\">Stop Server</button></div></section>"
        "<section class=\"panel\"><pre id=\"out\">Ready on port 1999.</pre></section></div>"
        "<script>"
        "async function api(u){let r=await fetch(u);out.textContent=JSON.stringify(await r.json(),null,2)}"
        "function qs(){return 'value='+encodeURIComponent(value.value)+'&type='+type.value+'&filter='+encodeURIComponent(filter.value)+'&aligned=1'}"
        "function scan(){api('/api/scan?'+qs())}function nextScan(){api('/api/next?value='+encodeURIComponent(value.value))}"
        "function writeAddr(){api('/api/write?addr='+encodeURIComponent(addr.value)+'&type='+type.value+'&value='+encodeURIComponent(writeInput.value))}"
        "function readAddr(){api('/api/read?addr='+encodeURIComponent(addr.value)+'&len=16')}"
        "</script></main></body></html>");
}

static void send_index(int client)
{
    send_response_bytes(client, "200 OK", "text/html; charset=utf-8", WEB_UI, WEB_UI_LENGTH);
}

static void send_status(int client)
{
    eboot_info_t eboot;
    bool found = find_eboot(&eboot);
    scan_job_t scan;
    pthread_mutex_lock(&g_state_lock);
    scan = g_scan;
    pthread_mutex_unlock(&g_state_lock);
    char body[768];
    snprintf(body, sizeof(body),
        "{\"app\":\"PS5MemoryPeeker-Web\",\"phase\":\"complete-web-build\",\"runtime\":\"ps5-payload-sdk\","
        "\"server\":\"running\",\"port\":%d,\"lastErrno\":%d,\"ebootFound\":%s,\"pid\":%d,"
        "\"titleId\":\"%s\",\"cachedResults\":%llu,\"scanState\":\"%s\"}",
        HTTP_PORT,
        g_last_errno,
        found ? "true" : "false",
        found ? eboot.pid : -1,
        found ? eboot.title_id : "",
        (unsigned long long)g_result_count,
        scan_state_name(scan.state));
    send_response(client, "200 OK", "application/json", body);
}

static void handle_client(int client)
{
    char req[REQ_BUFFER_SIZE];
    char target[1024];
    char *query = NULL;
    int read_len = (int)read(client, req, sizeof(req) - 1);
    if (read_len <= 0) {
        return;
    }
    req[read_len] = 0;

    char *start = strchr(req, ' ');
    if (!start) return;
    start++;
    char *end = strchr(start, ' ');
    if (!end) return;
    size_t len = (size_t)(end - start);
    if (len >= sizeof(target)) len = sizeof(target) - 1;
    memcpy(target, start, len);
    target[len] = 0;

    query = strchr(target, '?');
    if (query) {
        *query++ = 0;
    }

    if (!strcmp(target, "/") || !strcmp(target, "/index.html")) {
        send_index(client);
    } else if (!strcmp(target, "/assets/music.mp3")) {
        send_response_bytes(client, "200 OK", "audio/mpeg", BACKGROUND_MUSIC_MP3, BACKGROUND_MUSIC_MP3_LENGTH);
    } else if (!strcmp(target, "/assets/fold.mp3")) {
        send_response_bytes(client, "200 OK", "audio/mpeg", FOLD_SOUND_MP3, FOLD_SOUND_MP3_LENGTH);
    } else if (!strcmp(target, "/legacy")) {
        send_legacy_index(client);
    } else if (!strcmp(target, "/api/status")) {
        send_status(client);
    } else if (!strcmp(target, "/api/health")) {
        send_response(client, "200 OK", "application/json", "{\"ok\":true}");
    } else if (!strcmp(target, "/api/eboot")) {
        send_eboot(client);
    } else if (!strcmp(target, "/api/maps")) {
        send_maps(client, query);
    } else if (!strcmp(target, "/api/sections/select")) {
        send_section_select(client, query, false);
    } else if (!strcmp(target, "/api/sections/select-all")) {
        send_section_select(client, query, true);
    } else if (!strcmp(target, "/api/read")) {
        send_read(client, query);
    } else if (!strcmp(target, "/api/write")) {
        send_write(client, query);
    } else if (!strcmp(target, "/api/scan/start")) {
        send_scan_start(client, query, 1);
    } else if (!strcmp(target, "/api/scan/next/start")) {
        send_scan_start(client, query, 2);
    } else if (!strcmp(target, "/api/scan/progress")) {
        send_scan_progress(client);
    } else if (!strcmp(target, "/api/scan/stop")) {
        send_scan_stop(client);
    } else if (!strcmp(target, "/api/scan/legacy")) {
        send_scan(client, query);
    } else if (!strcmp(target, "/api/next/legacy")) {
        send_next(client, query);
    } else if (!strncmp(target, "/api/process/", 13)) {
        send_process_action(client, target + 13);
    } else if (!strcmp(target, "/api/results")) {
        send_results_safe(client);
    } else if (!strcmp(target, "/api/results/legacy")) {
        send_results(client);
    } else if (!strcmp(target, "/api/stop")) {
        g_running = 0;
        send_response(client, "200 OK", "application/json", "{\"stopping\":true}");
    } else {
        send_response(client, "404 Not Found", "application/json", "{\"error\":\"not_found\"}");
    }
}

static int run_http_server(void)
{
    int server = socket(AF_INET, SOCK_STREAM, 0);
    if (server < 0) {
        g_last_errno = errno;
        notify_ps5("socket failed");
        return -1;
    }

    int reuse = 1;
    setsockopt(server, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(HTTP_PORT);
    addr.sin_addr.s_addr = INADDR_ANY;

    if (bind(server, (const struct sockaddr *)&addr, sizeof(addr)) < 0) {
        g_last_errno = errno;
        close(server);
        notify_ps5("bind failed on port 1999");
        return -2;
    }

    if (listen(server, 8) < 0) {
        g_last_errno = errno;
        close(server);
        notify_ps5("listen failed on port 1999");
        return -3;
    }

    notify_ps5("memory backend started on port 1999");

    while (g_running) {
        int client = accept(server, 0, 0);
        if (client >= 0) {
            handle_client(client);
            close(client);
        }
    }

    close(server);
    notify_ps5("stopped");
    return 0;
}

int main(void)
{
    notify_ps5("payload loaded");
    return run_http_server();
}
