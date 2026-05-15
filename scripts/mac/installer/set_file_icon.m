#import <Cocoa/Cocoa.h>

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        if (argc != 3) {
            fprintf(stderr, "usage: set_file_icon <target-file> <icon-file>\n");
            return 2;
        }

        NSString *targetPath = [NSString stringWithUTF8String:argv[1]];
        NSString *iconPath = [NSString stringWithUTF8String:argv[2]];
        NSImage *icon = [[NSImage alloc] initWithContentsOfFile:iconPath];

        if (icon == nil) {
            fprintf(stderr, "failed to read icon: %s\n", argv[2]);
            return 3;
        }

        BOOL ok = [[NSWorkspace sharedWorkspace] setIcon:icon forFile:targetPath options:0];
        if (!ok) {
            fprintf(stderr, "failed to set icon: %s\n", argv[1]);
            return 4;
        }
    }

    return 0;
}
