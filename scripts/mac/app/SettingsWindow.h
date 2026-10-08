#import <Cocoa/Cocoa.h>
extern NSString *const MDBackupDefaultsKey;
@interface SettingsWindow : NSWindowController
@property(copy) void (^onInstall)(void);
@property(copy) void (^onRemove)(void);
@property(copy) void (^onBackupChanged)(BOOL);
@property NSButton *backup, *install, *remove;
@property NSTextField *status, *statusHint;
@property NSImageView *statusIcon;
- (void)refreshBusy:(BOOL)busy notice:(NSString *)notice;
@end
