/*
 * drmfault — makes a real AMD VAAPI encode fail mid-stream, on demand, inside ONE
 * ffmpeg process, without touching the host GPU (media bench, #342; method from
 * tesserafin#119 / docs/hardware-transcode-recovery.md).
 *
 * LD_PRELOADed into ffmpeg by the bench's wrapper. It interposes ioctl(2) and only
 * ever touches DRM_IOCTL_AMDGPU_CS (type 'd', nr 0x44) on a DRM character device
 * (major 226):
 *
 *   DRMFAULT_SLOW_US=<n>   sleep n microseconds before each command submission, so
 *                          an unthrottled hardware encode stays near the playhead
 *                          (25000 ~ 0.5-0.8x realtime on a Renoir iGPU);
 *   DRMFAULT_TRIGGER=<f>   while file f exists, fail the submission with ECANCELED.
 *                          radeonsi then prints "amdgpu: The CS has cancelled
 *                          because the context is lost" and aborts — the evidence
 *                          FfmpegErrorClassifier maps to HardwareDeviceLost.
 *
 * Failing EVERY DRM ioctl with ENODEV mid-stream is deliberately not offered: Mesa
 * segfaults with no diagnostic line, which the server correctly refuses to treat
 * as a hardware failure, so it would prove nothing about recovery.
 */
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <stdarg.h>
#include <stdlib.h>
#include <sys/stat.h>
#include <sys/sysmacros.h>
#include <unistd.h>

typedef int (*ioctl_fn)(int, unsigned long, ...);

int ioctl(int fd, unsigned long request, ...)
{
    static ioctl_fn real;
    va_list ap;
    void *arg;

    va_start(ap, request);
    arg = va_arg(ap, void *);
    va_end(ap);

    if (!real)
        real = (ioctl_fn)dlsym(RTLD_NEXT, "ioctl");

    if (((request >> 8) & 0xff) == 'd' && (request & 0xff) == 0x44) {
        struct stat st;
        if (fstat(fd, &st) == 0 && S_ISCHR(st.st_mode) && major(st.st_rdev) == 226) {
            const char *slow = getenv("DRMFAULT_SLOW_US");
            const char *trig = getenv("DRMFAULT_TRIGGER");
            if (slow && *slow)
                usleep((useconds_t)strtoul(slow, NULL, 10));
            if (trig && *trig && access(trig, F_OK) == 0) {
                errno = ECANCELED;
                return -1;
            }
        }
    }
    return real(fd, request, arg);
}
