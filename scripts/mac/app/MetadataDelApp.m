#import <Cocoa/Cocoa.h>
#import "FileDropView.h"
#import "CleaningRunner.h"
#import "IntegrationManager.h"

@interface AppDelegate : NSObject <NSApplicationDelegate,NSWindowDelegate,NSTableViewDataSource,NSTableViewDelegate>
@property NSWindow *window;
@property NSTableView *table;
@property NSMutableArray<NSMutableDictionary *> *files;
@property NSArray<NSString *> *initialFiles;
@property NSButton *choose, *clean, *remove, *reveal, *backup, *settings, *integration;
@property NSTextField *summary, *detail;
@property NSProgressIndicator *progress;
@property FileDropView *drop;
@property CleaningRunner *runner;
@property BOOL busy, closeAfterWork;
@property NSUInteger completed, successes, warnings;
@end
@implementation AppDelegate
- (NSTextField *)label:(NSString *)text size:(CGFloat)size weight:(NSFontWeight)weight {
    NSTextField *label=[NSTextField wrappingLabelWithString:text]; label.font=[NSFont systemFontOfSize:size weight:weight];
    label.textColor=NSColor.labelColor; return label;
}
- (NSButton *)button:(NSString *)title action:(SEL)action {
    NSButton *button=[NSButton buttonWithTitle:title target:self action:action]; button.bezelStyle=NSBezelStyleRounded;
    button.controlSize=NSControlSizeLarge; return button;
}
- (NSStackView *)stack:(NSArray<NSView *> *)views vertical:(BOOL)vertical {
    NSStackView *stack=[NSStackView stackViewWithViews:views]; stack.orientation=vertical ? NSUserInterfaceLayoutOrientationVertical : NSUserInterfaceLayoutOrientationHorizontal;
    stack.alignment=vertical ? NSLayoutAttributeLeading : NSLayoutAttributeCenterY; stack.spacing=12; return stack;
}
- (NSView *)spacer { NSView *view=[NSView new]; [view setContentHuggingPriority:1 forOrientation:NSLayoutConstraintOrientationHorizontal]; return view; }
- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    self.files=[NSMutableArray new];
    self.runner=[[CleaningRunner alloc] initWithExecutableURL:[NSURL fileURLWithPath:[NSBundle.mainBundle.resourcePath stringByAppendingPathComponent:@"bin/metadatadel"]]];
    [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular]; [self buildMenu]; [self buildWindow];
    [self.window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES];
    if (self.initialFiles) [self addURLs:[self.initialFiles valueForKey:@"stringByStandardizingPath"]];
    [self refresh];
    NSString *path=NSBundle.mainBundle.bundlePath;
    if ([IntegrationManager needsUpdate] && ([path hasPrefix:@"/Applications/"] || [path hasPrefix:[NSHomeDirectory() stringByAppendingString:@"/Applications/"]])) [self installIntegration:nil];
}
- (void)buildMenu {
    NSMenu *menu=[NSMenu new]; NSMenuItem *app=[NSMenuItem new]; [menu addItem:app];
    NSMenu *submenu=[NSMenu new]; [submenu addItemWithTitle:@"О программе MetadataDel" action:@selector(orderFrontStandardAboutPanel:) keyEquivalent:@""];
    [submenu addItem:NSMenuItem.separatorItem]; [submenu addItemWithTitle:@"Завершить MetadataDel" action:@selector(terminate:) keyEquivalent:@"q"]; app.submenu=submenu;
    NSMenuItem *file=[NSMenuItem new]; [menu addItem:file]; NSMenu *fileMenu=[[NSMenu alloc] initWithTitle:@"Файл"];
    NSMenuItem *open=[fileMenu addItemWithTitle:@"Выбрать файлы…" action:@selector(pick:) keyEquivalent:@"o"]; open.target=self;
    [fileMenu addItemWithTitle:@"Закрыть окно" action:@selector(performClose:) keyEquivalent:@"w"]; file.submenu=fileMenu;
    NSMenuItem *edit=[NSMenuItem new]; [menu addItem:edit]; NSMenu *editMenu=[[NSMenu alloc] initWithTitle:@"Правка"];
    [editMenu addItemWithTitle:@"Копировать" action:@selector(copy:) keyEquivalent:@"c"];
    [editMenu addItemWithTitle:@"Вставить" action:@selector(paste:) keyEquivalent:@"v"];
    [editMenu addItemWithTitle:@"Выбрать всё" action:@selector(selectAll:) keyEquivalent:@"a"]; edit.submenu=editMenu;
    NSApp.mainMenu=menu;
}
- (void)buildWindow {
    self.window=[[NSWindow alloc] initWithContentRect:NSMakeRect(0,0,760,700) styleMask:NSWindowStyleMaskTitled|NSWindowStyleMaskClosable|NSWindowStyleMaskMiniaturizable|NSWindowStyleMaskResizable backing:NSBackingStoreBuffered defer:NO];
    self.window.title=@"MetadataDel"; self.window.minSize=NSMakeSize(720,650); self.window.delegate=self; [self.window center];
    self.window.backgroundColor=NSColor.windowBackgroundColor;
    NSImageView *icon=[NSImageView new]; icon.image=NSApp.applicationIconImage;
    [icon.widthAnchor constraintEqualToConstant:44].active=YES; [icon.heightAnchor constraintEqualToConstant:44].active=YES;
    NSStackView *brand=[self stack:@[[self label:@"MetadataDel" size:24 weight:NSFontWeightSemibold],[self label:@"Документы без лишних метаданных" size:13 weight:NSFontWeightRegular]] vertical:YES]; brand.spacing=4;
    self.settings=[self button:@"Настройки" action:@selector(showSettings:)];
    NSStackView *header=[self stack:@[icon,brand,[self spacer],self.settings] vertical:NO];
    self.integration=[self button:@"Включить очистку правым кликом в Finder" action:@selector(installIntegration:)]; self.integration.controlSize=NSControlSizeRegular;
    self.drop=[[FileDropView alloc] initWithFrame:NSZeroRect]; self.drop.translatesAutoresizingMaskIntoConstraints=NO;
    self.choose=[self button:@"Выбрать файлы…" action:@selector(pick:)];
    NSTextField *formats=[self label:@"PDF · DOCX · XLSX · DOC · XLS" size:12 weight:NSFontWeightRegular]; formats.textColor=NSColor.secondaryLabelColor;
    NSStackView *dropContents=[self stack:@[[self label:@"Перетащите сюда документы" size:20 weight:NSFontWeightMedium],formats,self.choose] vertical:YES];
    dropContents.alignment=NSLayoutAttributeCenterX; dropContents.translatesAutoresizingMaskIntoConstraints=NO; [self.drop addSubview:dropContents];
    [NSLayoutConstraint activateConstraints:@[[dropContents.centerXAnchor constraintEqualToAnchor:self.drop.centerXAnchor],[dropContents.centerYAnchor constraintEqualToAnchor:self.drop.centerYAnchor],[self.drop.heightAnchor constraintEqualToConstant:162]]];
    __weak AppDelegate *weakSelf=self; self.drop.onFiles=^(NSArray<NSURL *> *urls){ [weakSelf addURLs:urls]; };
    self.summary=[self label:@"Добавьте документы — обработка начнётся по кнопке." size:12 weight:NSFontWeightMedium];
    self.table=[NSTableView new]; self.table.delegate=self; self.table.dataSource=self; self.table.rowHeight=40; self.table.allowsMultipleSelection=YES;
    self.table.style=NSTableViewStyleFullWidth; self.table.usesAlternatingRowBackgroundColors=NO;
    NSTableColumn *name=[[NSTableColumn alloc] initWithIdentifier:@"name"]; name.title=@"Документ"; name.width=370; name.minWidth=230;
    NSTableColumn *state=[[NSTableColumn alloc] initWithIdentifier:@"state"]; state.title=@"Результат"; state.width=270; state.minWidth=220;
    [self.table addTableColumn:name]; [self.table addTableColumn:state]; self.table.columnAutoresizingStyle=NSTableViewLastColumnOnlyAutoresizingStyle;
    NSScrollView *scroll=[NSScrollView new]; scroll.documentView=self.table; scroll.hasVerticalScroller=YES; scroll.borderType=NSNoBorder; scroll.drawsBackground=NO;
    [scroll.heightAnchor constraintGreaterThanOrEqualToConstant:110].active=YES; [scroll setContentHuggingPriority:1 forOrientation:NSLayoutConstraintOrientationVertical];
    self.remove=[self button:@"Убрать выбранные" action:@selector(removeFiles:)]; self.reveal=[self button:@"Показать в Finder" action:@selector(revealFile:)];
    NSStackView *actions=[self stack:@[self.remove,self.reveal,[self spacer]] vertical:NO];
    self.detail=[self label:@"Правый клик в Finder работает независимо от этого окна." size:12 weight:NSFontWeightRegular]; self.detail.textColor=NSColor.secondaryLabelColor;
    self.detail.maximumNumberOfLines=3; self.detail.selectable=YES;
    self.backup=[NSButton checkboxWithTitle:@"Создавать резервные копии" target:nil action:nil]; self.backup.state=NSControlStateValueOn;
    NSTextField *notice=[self label:@"Исходные файлы будут заменены после очистки." size:11 weight:NSFontWeightRegular]; notice.textColor=NSColor.secondaryLabelColor;
    NSStackView *options=[self stack:@[self.backup,notice] vertical:YES]; options.spacing=3;
    self.clean=[self button:@"Очистить файлы" action:@selector(cleanFiles:)]; self.clean.bezelColor=[NSColor colorWithSRGBRed:0.69 green:0.27 blue:0.14 alpha:1];
    NSStackView *footer=[self stack:@[options,[self spacer],self.clean] vertical:NO];
    self.progress=[NSProgressIndicator new]; self.progress.indeterminate=NO; self.progress.style=NSProgressIndicatorStyleBar;
    NSStackView *root=[self stack:@[header,self.integration,self.drop,self.summary,scroll,actions,self.detail,self.progress,footer] vertical:YES];
    root.translatesAutoresizingMaskIntoConstraints=NO; root.spacing=14; [self.window.contentView addSubview:root];
    [NSLayoutConstraint activateConstraints:@[[root.leadingAnchor constraintEqualToAnchor:self.window.contentView.leadingAnchor constant:28],[root.trailingAnchor constraintEqualToAnchor:self.window.contentView.trailingAnchor constant:-28],[root.topAnchor constraintEqualToAnchor:self.window.contentView.topAnchor constant:22],[root.bottomAnchor constraintEqualToAnchor:self.window.contentView.bottomAnchor constant:-24]]];
    for (NSView *view in @[header,self.drop,scroll,actions,self.detail,self.progress,footer]) [view.widthAnchor constraintEqualToAnchor:root.widthAnchor].active=YES;
}
- (void)addURLs:(NSArray *)urls {
    if (self.busy) return;
    NSMutableSet *known=[NSMutableSet new]; for (NSDictionary *file in self.files) [known addObject:file[@"path"]];
    for (id input in urls) {
        NSURL *url=[input isKindOfClass:NSURL.class] ? input : [NSURL fileURLWithPath:input]; if (!url.isFileURL) continue;
        NSString *path=url.path.stringByStandardizingPath; if ([known containsObject:path]) continue; [known addObject:path];
        BOOL directory=NO; [NSFileManager.defaultManager fileExistsAtPath:path isDirectory:&directory];
        BOOL supported=[@[@"pdf",@"docx",@"xlsx",@"doc",@"xls"] containsObject:path.pathExtension.lowercaseString];
        NSString *state=directory ? @"Для папок используйте правый клик" : supported ? @"Готов к очистке" : @"Формат не поддерживается";
        [self.files addObject:[@{@"path":path,@"state":state,@"attempted":@(directory || !supported),@"message":path} mutableCopy]];
    }
    [self.table reloadData]; [self refresh];
}
- (NSInteger)numberOfRowsInTableView:(NSTableView *)tableView { return self.files.count; }
- (NSView *)tableView:(NSTableView *)tableView viewForTableColumn:(NSTableColumn *)column row:(NSInteger)row {
    NSDictionary *file=self.files[row]; BOOL name=[column.identifier isEqual:@"name"];
    NSTextField *text=[NSTextField labelWithString:name ? [file[@"path"] lastPathComponent] : file[@"state"]];
    text.font=[NSFont systemFontOfSize:13 weight:name ? NSFontWeightMedium : NSFontWeightRegular];
    text.lineBreakMode=NSLineBreakByTruncatingMiddle; text.toolTip=file[@"message"];
    if (!name) text.textColor=[file[@"state"] isEqual:@"Очищен"] ? NSColor.systemGreenColor : NSColor.secondaryLabelColor;
    return text;
}
- (void)tableViewSelectionDidChange:(NSNotification *)notification {
    NSInteger row=self.table.selectedRow; if (row>=0 && row<self.files.count) self.detail.stringValue=self.files[row][@"message"];
    [self refresh];
}
- (void)refresh {
    NSUInteger pending=0; for (NSDictionary *file in self.files) if (![file[@"attempted"] boolValue]) pending++;
    self.clean.enabled=!self.busy && pending>0; self.clean.title=self.busy ? @"Обработка…" : pending ? [NSString stringWithFormat:@"Очистить · %lu",pending] : @"Очистить файлы";
    self.choose.enabled=self.backup.enabled=self.settings.enabled=self.integration.enabled=self.drop.enabled=!self.busy;
    self.remove.enabled=!self.busy && self.table.selectedRowIndexes.count>0; self.reveal.enabled=!self.busy && self.table.selectedRowIndexes.count==1;
    self.integration.title=[IntegrationManager isInstalled] ? @"✓ Очистка правым кликом в Finder включена" : [IntegrationManager needsUpdate] ? @"Восстановить очистку правым кликом в Finder" : @"Включить очистку правым кликом в Finder";
    if (!self.busy) self.summary.stringValue=self.files.count ? [NSString stringWithFormat:@"В списке: %lu · Готовы к очистке: %lu",self.files.count,pending] : @"Добавьте документы — обработка начнётся по кнопке.";
}
- (void)pick:(id)sender {
    if (self.busy) return;
    NSOpenPanel *panel=[NSOpenPanel openPanel]; panel.canChooseFiles=YES; panel.canChooseDirectories=NO; panel.allowsMultipleSelection=YES;
    panel.message=@"Выберите PDF, Word или Excel-документы";
    [panel beginSheetModalForWindow:self.window completionHandler:^(NSModalResponse response){ if (response==NSModalResponseOK) [self addURLs:panel.URLs]; }];
}
- (void)removeFiles:(id)sender { if (!self.busy) { [self.files removeObjectsAtIndexes:self.table.selectedRowIndexes]; [self.table reloadData]; [self refresh]; } }
- (void)revealFile:(id)sender {
    NSInteger row=self.table.selectedRow; if (row<0) return; NSDictionary *file=self.files[row];
    [NSWorkspace.sharedWorkspace activateFileViewerSelectingURLs:@[[NSURL fileURLWithPath:file[@"output"] ?: file[@"path"]]]];
}
- (void)cleanFiles:(id)sender {
    if (self.busy || !self.clean.enabled) return; self.busy=YES; self.completed=self.successes=self.warnings=0;
    self.progress.doubleValue=0; self.progress.maxValue=0; for (NSDictionary *file in self.files) if (![file[@"attempted"] boolValue]) self.progress.maxValue++;
    [self refresh]; [self cleanNext];
}
- (void)cleanNext {
    NSMutableDictionary *next=nil; for (NSMutableDictionary *file in self.files) if (![file[@"attempted"] boolValue]) { next=file; break; }
    if (!next) {
        self.busy=NO; [self refresh]; self.summary.stringValue=[NSString stringWithFormat:@"Очищено: %lu из %lu · С предупреждениями: %lu",self.successes,self.completed,self.warnings];
        if (self.closeAfterWork) [NSApp terminate:nil]; return;
    }
    next[@"attempted"]=@YES; next[@"state"]=@"Обрабатывается…";
    self.summary.stringValue=[NSString stringWithFormat:@"Обработка %lu из %.0f",self.completed+1,self.progress.maxValue]; [self.table reloadData];
    [self.runner cleanFileURL:[NSURL fileURLWithPath:next[@"path"]] backup:self.backup.state==NSControlStateValueOn completion:^(NSDictionary *result,NSError *error){
        BOOL success=!error && [result[@"success"] boolValue]; NSString *message=[result[@"message"] isKindOfClass:NSString.class] ? result[@"message"] : nil;
        BOOL warning=success && message.length>0; if (success) self.successes++; if (warning) self.warnings++;
        next[@"state"]=success ? warning ? @"Очищен с предупреждениями" : @"Очищен" : @"Не обработан";
        if (success) next[@"output"]=result[@"outputPath"];
        next[@"message"]=error.localizedDescription ?: message ?: result[@"outputPath"] ?: @"Не удалось обработать файл.";
        self.detail.stringValue=next[@"message"]; self.progress.doubleValue=++self.completed; [self.table reloadData]; [self cleanNext];
    }];
}
- (void)installIntegration:(id)sender {
    if (self.busy) return; self.busy=YES; [self refresh]; self.detail.stringValue=@"Настраиваю очистку правым кликом…";
    [IntegrationManager installWithCompletion:^(NSError *error){ self.busy=NO; [self refresh]; self.detail.stringValue=error.localizedDescription ?: @"Готово. Finder → Быстрые действия → Удалить метаданные (MetadataDel)."; if (self.closeAfterWork) [NSApp terminate:nil]; }];
}
- (void)showSettings:(id)sender {
    NSAlert *alert=[NSAlert new]; alert.messageText=@"MetadataDel · Настройки";
    alert.informativeText=@"Правый клик в Finder работает без открытого окна. Резервные копии для него всегда включены.\n\nЗдесь можно восстановить интеграцию или удалить команды и действие Finder. Документы и их копии сохранятся. Для удаления самого приложения переместите его в Корзину.";
    [alert addButtonWithTitle:@"Готово"]; [alert addButtonWithTitle:@"Восстановить Finder"]; [alert addButtonWithTitle:@"Удалить интеграцию"];
    [alert beginSheetModalForWindow:self.window completionHandler:^(NSModalResponse response){
        if (response==NSAlertSecondButtonReturn) [self installIntegration:nil];
        else if (response==NSAlertThirdButtonReturn) {
            self.busy=YES; [self refresh]; [IntegrationManager removeWithCompletion:^(NSError *error){ self.busy=NO; [self refresh]; self.detail.stringValue=error.localizedDescription ?: @"Действие Finder и команды удалены. Документы сохранены."; if (self.closeAfterWork) [NSApp terminate:nil]; }];
        }
    }];
}
- (BOOL)windowShouldClose:(NSWindow *)sender { if (self.busy) { self.closeAfterWork=YES; self.detail.stringValue=@"Окно закроется после завершения обработки."; return NO; } return YES; }
- (NSApplicationTerminateReply)applicationShouldTerminate:(NSApplication *)sender { return [self windowShouldClose:self.window] ? NSTerminateNow : NSTerminateCancel; }
- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)sender { return YES; }
- (void)application:(NSApplication *)sender openFiles:(NSArray<NSString *> *)filenames {
    if (self.busy) { self.detail.stringValue=@"Дождитесь завершения обработки и добавьте новые файлы ещё раз."; [sender replyToOpenOrPrint:NSApplicationDelegateReplyFailure]; return; }
    if (!self.files) self.initialFiles=filenames; else [self addURLs:filenames]; [sender replyToOpenOrPrint:NSApplicationDelegateReplySuccess];
}
@end
int main(int argc,const char **argv) { @autoreleasepool { NSApplication *app=NSApplication.sharedApplication; AppDelegate *delegate=[AppDelegate new]; app.delegate=delegate; [app run]; } return 0; }
