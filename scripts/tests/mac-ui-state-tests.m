#define main MetadataDelApplicationMain
#import "../mac/app/MetadataDelApp.m"
#undef main

@interface PreviewRunner : CleaningRunner
@end
@implementation PreviewRunner
- (void)cleanFileURL:(NSURL *)url backup:(BOOL)backup completion:(void (^)(NSDictionary *, NSError *))completion {
    BOOL ok=[url.lastPathComponent isEqual:@"good.docx"];
    completion(@{@"success":@(ok), @"outputPath":ok ? url.path : NSNull.null, @"message":ok ? NSNull.null : @"Файл повреждён. Выберите другую копию документа."},nil);
}
@end
@interface SettingsWindow (Tests)
- (void)backupClicked:(id)sender;
@end
int main(void) { @autoreleasepool {
    [NSApplication sharedApplication]; [NSUserDefaults.standardUserDefaults removeObjectForKey:MDBackupDefaultsKey];
    AppDelegate *app=[AppDelegate new]; app.files=[NSMutableArray new];
    app.runner=[PreviewRunner new]; [app buildWindow];
    [app addURLs:@[@"/tmp/good.docx",@"/tmp/bad.docx"]];
    [app cleanFiles:nil];
    NSString *result=[app.summary.stringValue copy];
    [app.table selectRowIndexes:[NSIndexSet indexSetWithIndex:1] byExtendingSelection:NO];
    [app tableViewSelectionDidChange:[NSNotification notificationWithName:NSTableViewSelectionDidChangeNotification object:app.table]];
    NSCAssert([app.summary.stringValue isEqual:result], @"Selecting a result must preserve the batch summary");
    NSCAssert([result containsString:@"Не обработано: 1"], @"Mixed result must state failure count explicitly");
    NSCAssert(!app.clean.enabled,@"Completed files cannot be cleaned again implicitly");
    [app.table selectAll:nil]; [app removeFiles:nil];
    NSCAssert(app.files.count==0,@"Remove only empties queue");
    NSCAssert(app.progress.hidden,@"Empty queue has no stale progress bar");
    NSCAssert([app.table enclosingScrollView].hidden,@"Empty state has no empty table");
    app.windowDrop.onFiles(@[[NSURL fileURLWithPath:@"/tmp/client-a/document.docx"],[NSURL fileURLWithPath:@"/tmp/client-b/document.docx"],[NSURL fileURLWithPath:@"/tmp/client-a/document.docx"]]);
    NSCAssert(app.files.count==2,@"Window drop preserves distinct paths and ignores exact duplicates");
    app.busy=YES; [app refresh];
    app.windowDrop.onFiles(@[[NSURL fileURLWithPath:@"/tmp/later.docx"]]);
    NSCAssert(app.files.count==2 && !app.windowDrop.enabled && !app.drop.enabled,@"Busy state blocks all drop targets");
    app.busy=NO; [app clearFiles:nil];
    app.backup.state=NSControlStateValueOff; [app backupChanged:nil];
    NSCAssert([app.backupHint.stringValue containsString:@"Без резервных копий"],@"Backup-off state is explicit");
    NSCAssert(![NSUserDefaults.standardUserDefaults boolForKey:MDBackupDefaultsKey],@"Backup choice persists between launches");
    [app showSettings:nil];
    NSCAssert(app.settingsWindow.backup.state==NSControlStateValueOff,@"Settings mirror the main window backup choice");
    app.settingsWindow.backup.state=NSControlStateValueOn; [app.settingsWindow backupClicked:nil];
    NSCAssert(app.backup.state==NSControlStateValueOn && [app.backupHint.stringValue containsString:@".bak"],@"Settings update the main window backup choice");
    [app.settingsWindow close]; [NSUserDefaults.standardUserDefaults removeObjectForKey:MDBackupDefaultsKey];
    [app addURLs:@[@"/tmp/bad.docx"]]; [app cleanFiles:nil];
    StatusCellView *cell=(StatusCellView *)[app tableView:app.table viewForTableColumn:app.table.tableColumns[1] row:0];
    cell.backgroundStyle=NSBackgroundStyleEmphasized;
    NSCAssert([cell.statusLabel.textColor isEqual:NSColor.alternateSelectedControlTextColor],@"Selected failure uses readable system text");
    puts("Desktop UI: summary, selection, empty state, distinct paths, busy drop, backup copy, settings sync and contrast PASS");
} return 0; }
