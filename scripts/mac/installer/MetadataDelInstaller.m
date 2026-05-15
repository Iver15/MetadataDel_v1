#import <Cocoa/Cocoa.h>

@interface InstallerAppDelegate : NSObject <NSApplicationDelegate>
@property(nonatomic, strong) NSWindow *window;
@property(nonatomic, strong) NSTextField *statusLabel;
@property(nonatomic, strong) NSTextField *detailLabel;
@property(nonatomic, strong) NSTextView *logView;
@property(nonatomic, strong) NSButton *installButton;
@property(nonatomic, strong) NSButton *uninstallButton;
@property(nonatomic, strong) NSButton *logsButton;
@property(nonatomic) BOOL working;
@end

@implementation InstallerAppDelegate

- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
    [self buildWindow];
    [self updateStatus];
    [self.window makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
}

- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)sender {
    return YES;
}

- (NSString *)homePath {
    return NSHomeDirectory();
}

- (NSString *)installDir {
    return [[self homePath] stringByAppendingPathComponent:@".local/bin"];
}

- (NSString *)commandPath {
    return [[self installDir] stringByAppendingPathComponent:@"metadatadel"];
}

- (NSString *)aliasPath {
    return [[self installDir] stringByAppendingPathComponent:@"mdel"];
}

- (NSString *)workflowPath {
    return [[[self homePath] stringByAppendingPathComponent:@"Library/Services"] stringByAppendingPathComponent:@"Удалить метаданные (MetadataDel).workflow"];
}

- (NSString *)logsPath {
    return [[self homePath] stringByAppendingPathComponent:@"Library/Logs/MetadataDel"];
}

- (NSString *)resourcePath:(NSString *)relativePath {
    return [[[NSBundle mainBundle] resourcePath] stringByAppendingPathComponent:relativePath];
}

- (NSTextField *)labelWithText:(NSString *)text size:(CGFloat)size weight:(NSFontWeight)weight color:(NSColor *)color frame:(NSRect)frame {
    NSTextField *label = [NSTextField labelWithString:text];
    label.frame = frame;
    label.font = [NSFont systemFontOfSize:size weight:weight];
    label.textColor = color;
    label.lineBreakMode = NSLineBreakByWordWrapping;
    return label;
}

- (NSButton *)buttonWithTitle:(NSString *)title color:(NSColor *)color frame:(NSRect)frame action:(SEL)action {
    NSButton *button = [NSButton buttonWithTitle:title target:self action:action];
    button.frame = frame;
    button.bordered = NO;
    button.wantsLayer = YES;
    button.layer.cornerRadius = 8.0;
    button.layer.backgroundColor = color.CGColor;
    button.attributedTitle = [[NSAttributedString alloc] initWithString:title attributes:@{
        NSForegroundColorAttributeName: NSColor.whiteColor,
        NSFontAttributeName: [NSFont systemFontOfSize:14 weight:NSFontWeightSemibold]
    }];
    return button;
}

- (void)buildWindow {
    self.window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 660, 470)
                                             styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable
                                               backing:NSBackingStoreBuffered
                                                 defer:NO];
    [self.window center];
    self.window.title = @"Установщик MetadataDel";
    self.window.backgroundColor = NSColor.windowBackgroundColor;

    NSView *root = [[NSView alloc] initWithFrame:self.window.contentView.bounds];
    root.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    root.wantsLayer = YES;
    self.window.contentView = root;

    NSColor *accent = [NSColor colorWithCalibratedRed:0.91 green:0.45 blue:0.17 alpha:1.0];
    NSColor *dark = [NSColor colorWithCalibratedWhite:0.10 alpha:1.0];

    NSImageView *logo = [[NSImageView alloc] initWithFrame:NSMakeRect(34, 338, 84, 84)];
    logo.image = [[NSImage alloc] initWithContentsOfFile:[self resourcePath:@"AppIcon.icns"]];
    logo.imageScaling = NSImageScaleProportionallyUpOrDown;
    [root addSubview:logo];

    [root addSubview:[self labelWithText:@"MetadataDel" size:30 weight:NSFontWeightBold color:NSColor.labelColor frame:NSMakeRect(134, 385, 460, 38)]];
    [root addSubview:[self labelWithText:@"Простая установка удаления метаданных для Finder" size:15 weight:NSFontWeightRegular color:NSColor.secondaryLabelColor frame:NSMakeRect(136, 355, 480, 24)]];

    NSBox *separator = [[NSBox alloc] initWithFrame:NSMakeRect(34, 318, 592, 1)];
    separator.boxType = NSBoxSeparator;
    [root addSubview:separator];

    self.statusLabel = [self labelWithText:@"" size:13 weight:NSFontWeightMedium color:NSColor.secondaryLabelColor frame:NSMakeRect(34, 278, 592, 22)];
    self.detailLabel = [self labelWithText:@"" size:13 weight:NSFontWeightRegular color:NSColor.secondaryLabelColor frame:NSMakeRect(34, 244, 592, 44)];
    [root addSubview:self.statusLabel];
    [root addSubview:self.detailLabel];

    self.installButton = [self buttonWithTitle:@"Установить или обновить" color:accent frame:NSMakeRect(34, 190, 202, 42) action:@selector(installClicked:)];
    self.uninstallButton = [self buttonWithTitle:@"Удалить" color:dark frame:NSMakeRect(250, 190, 126, 42) action:@selector(uninstallClicked:)];
    self.logsButton = [self buttonWithTitle:@"Открыть журнал" color:NSColor.controlAccentColor frame:NSMakeRect(390, 190, 150, 42) action:@selector(openLogsClicked:)];
    [root addSubview:self.installButton];
    [root addSubview:self.uninstallButton];
    [root addSubview:self.logsButton];

    [root addSubview:[self labelWithText:@"После установки: Finder -> правый клик по файлу или папке -> Быстрые действия -> Удалить метаданные (MetadataDel)"
                                    size:12
                                  weight:NSFontWeightRegular
                                   color:NSColor.tertiaryLabelColor
                                   frame:NSMakeRect(34, 160, 592, 20)]];

    NSScrollView *scroll = [[NSScrollView alloc] initWithFrame:NSMakeRect(34, 28, 592, 118)];
    scroll.borderType = NSNoBorder;
    scroll.hasVerticalScroller = YES;
    scroll.wantsLayer = YES;
    scroll.layer.cornerRadius = 8.0;
    scroll.layer.backgroundColor = NSColor.textBackgroundColor.CGColor;

    self.logView = [[NSTextView alloc] initWithFrame:scroll.bounds];
    self.logView.editable = NO;
    self.logView.selectable = YES;
    self.logView.font = [NSFont monospacedSystemFontOfSize:12 weight:NSFontWeightRegular];
    self.logView.textColor = NSColor.secondaryLabelColor;
    self.logView.backgroundColor = NSColor.textBackgroundColor;
    self.logView.textContainerInset = NSMakeSize(10, 8);
    scroll.documentView = self.logView;
    [root addSubview:scroll];
}

- (void)setWorkingState:(BOOL)working {
    self.working = working;
    self.installButton.enabled = !working;
    self.uninstallButton.enabled = !working;
    self.logsButton.enabled = !working;
}

- (void)updateStatus {
    NSFileManager *fm = NSFileManager.defaultManager;
    BOOL commandInstalled = [fm isExecutableFileAtPath:[self commandPath]];
    BOOL workflowInstalled = [fm fileExistsAtPath:[self workflowPath]];

    if (commandInstalled && workflowInstalled) {
        self.statusLabel.stringValue = @"Статус: установлено";
        self.detailLabel.stringValue = [NSString stringWithFormat:@"Команда установлена: %@\nFinder Quick Action установлен: %@", [self commandPath], [self workflowPath]];
    } else if (commandInstalled || workflowInstalled) {
        self.statusLabel.stringValue = @"Статус: установлено частично";
        self.detailLabel.stringValue = @"Нажмите «Установить или обновить», чтобы привести установку в нормальное состояние.";
    } else {
        self.statusLabel.stringValue = @"Статус: не установлено";
        self.detailLabel.stringValue = @"Нажмите «Установить или обновить». Пароль администратора не нужен.";
    }
}

- (void)appendLog:(NSString *)text {
    dispatch_async(dispatch_get_main_queue(), ^{
        NSString *old = self.logView.string ?: @"";
        self.logView.string = old.length == 0 ? text : [old stringByAppendingFormat:@"\n%@", text];
        [self.logView scrollToEndOfDocument:nil];
    });
}

- (void)runTaskWithTitle:(NSString *)title block:(void (^)(NSError **error))block {
    if (self.working) {
        return;
    }

    [self setWorkingState:YES];
    self.logView.string = @"";
    [self appendLog:[title stringByAppendingString:@"..."]];

    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
        NSError *error = nil;
        block(&error);
        dispatch_async(dispatch_get_main_queue(), ^{
            if (error) {
                [self appendLog:[NSString stringWithFormat:@"Ошибка: %@", error.localizedDescription]];
                NSBeep();
            } else {
                [self appendLog:@"Операция завершена."];
            }

            [self setWorkingState:NO];
            [self updateStatus];
        });
    });
}

- (IBAction)installClicked:(id)sender {
    [self runTaskWithTitle:@"Установка" block:^(NSError **error) {
        [self installCommand:error];
        if (*error) return;
        [self runScript:[self resourcePath:@"scripts/mac/install-finder-action.sh"] error:error];
        if (*error) return;
        [self appendLog:@"Готово. Finder Quick Action установлен."];
    }];
}

- (IBAction)uninstallClicked:(id)sender {
    [self runTaskWithTitle:@"Удаление" block:^(NSError **error) {
        NSError *ignored = nil;
        [self runScript:[self resourcePath:@"scripts/mac/uninstall-finder-action.sh"] error:&ignored];
        [NSFileManager.defaultManager removeItemAtPath:[self commandPath] error:nil];
        [NSFileManager.defaultManager removeItemAtPath:[self aliasPath] error:nil];
        [self appendLog:@"Готово. Команда и Finder Quick Action удалены."];
    }];
}

- (IBAction)openLogsClicked:(id)sender {
    [NSFileManager.defaultManager createDirectoryAtPath:[self logsPath] withIntermediateDirectories:YES attributes:nil error:nil];
    [NSWorkspace.sharedWorkspace openURL:[NSURL fileURLWithPath:[self logsPath] isDirectory:YES]];
}

- (void)installCommand:(NSError **)error {
    NSFileManager *fm = NSFileManager.defaultManager;
    [fm createDirectoryAtPath:[self installDir] withIntermediateDirectories:YES attributes:nil error:error];
    if (*error) return;

    if ([fm fileExistsAtPath:[self commandPath]]) {
        [fm removeItemAtPath:[self commandPath] error:error];
        if (*error) return;
    }

    [fm copyItemAtPath:[self resourcePath:@"bin/metadatadel"] toPath:[self commandPath] error:error];
    if (*error) return;

    [fm setAttributes:@{NSFilePosixPermissions: @0755} ofItemAtPath:[self commandPath] error:error];
    if (*error) return;

    [self removeQuarantine:[self commandPath]];

    if ([fm fileExistsAtPath:[self aliasPath]]) {
        [fm removeItemAtPath:[self aliasPath] error:error];
        if (*error) return;
    }

    [fm createSymbolicLinkAtPath:[self aliasPath] withDestinationPath:@"metadatadel" error:error];
    if (*error) return;

    [self appendLog:[NSString stringWithFormat:@"Команда установлена: %@", [self commandPath]]];
    [self appendLog:[NSString stringWithFormat:@"Короткая команда установлена: %@", [self aliasPath]]];
}

- (void)runScript:(NSString *)path error:(NSError **)error {
    NSTask *task = [[NSTask alloc] init];
    task.executableURL = [NSURL fileURLWithPath:@"/bin/bash"];
    task.arguments = @[path];
    task.environment = @{
        @"HOME": [self homePath],
        @"PATH": @"/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin"
    };

    NSPipe *pipe = [NSPipe pipe];
    task.standardOutput = pipe;
    task.standardError = pipe;
    [task launchAndReturnError:error];
    if (*error) return;
    [task waitUntilExit];

    NSData *data = [[pipe fileHandleForReading] readDataToEndOfFile];
    NSString *output = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] ?: @"";
    NSString *trimmed = [output stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    if (trimmed.length > 0) {
        [self appendLog:trimmed];
    }

    if (task.terminationStatus != 0) {
        if (error) {
            *error = [NSError errorWithDomain:@"MetadataDelInstaller"
                                         code:task.terminationStatus
                                     userInfo:@{NSLocalizedDescriptionKey: [NSString stringWithFormat:@"Скрипт завершился с кодом %d", task.terminationStatus]}];
        }
    }
}

- (void)removeQuarantine:(NSString *)path {
    NSTask *task = [[NSTask alloc] init];
    task.executableURL = [NSURL fileURLWithPath:@"/usr/bin/xattr"];
    task.arguments = @[@"-d", @"com.apple.quarantine", path];
    [task launchAndReturnError:nil];
    [task waitUntilExit];
}

@end

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        NSApplication *app = NSApplication.sharedApplication;
        InstallerAppDelegate *delegate = [[InstallerAppDelegate alloc] init];
        app.delegate = delegate;
        [app run];
    }
    return 0;
}
