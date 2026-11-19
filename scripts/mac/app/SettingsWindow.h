#import <Cocoa/Cocoa.h>
extern NSString *const MDBackupDefaultsKey;
/// Private backups folder shared with the command line tool: ~/Library/Application Support/MetadataDel/Backups.
NSString *MDBackupsDirectory(void);
@interface SettingsWindow : NSWindowController
@property(copy) void (^onInstall)(void);
@property(copy) void (^onRemove)(void);
@property(copy) void (^onBackupChanged)(BOOL);
@property NSButton *backup, *install, *remove;
@property NSTextField *status, *statusHint;
@property NSImageView *statusIcon;
- (void)refreshBusy:(BOOL)busy notice:(NSString *)notice;
@end
