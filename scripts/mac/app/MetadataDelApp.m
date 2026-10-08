#import <Cocoa/Cocoa.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#import "FileDropView.h"
#import "CleaningRunner.h"
#import "IntegrationManager.h"

@interface StatusCellView : NSTableCellView
@property NSColor *statusColor;
@property NSTextField *statusLabel;
@property NSImageView *statusIcon;
@end
@implementation StatusCellView
- (void)setBackgroundStyle:(NSBackgroundStyle)style {
    [super setBackgroundStyle:style];
    NSColor *color=style==NSBackgroundStyleEmphasized ? NSColor.alternateSelectedControlTextColor : self.statusColor;
    self.statusLabel.textColor=color; self.statusIcon.contentTintColor=color;
}
@end

@interface AppDelegate : NSObject <NSApplicationDelegate,NSWindowDelegate,NSTableViewDataSource,NSTableViewDelegate>
@property NSWindow *window;
@property NSTableView *table;
@property NSMutableArray<NSMutableDictionary *> *files;
@property NSArray<NSString *> *initialFiles;
@property NSButton *choose, *clean, *remove, *clearList, *reveal, *backup, *settings, *integration;
@property NSTextField *summary, *dropTitle, *dropHint, *backupHint, *integrationStatus, *integrationNotice;
@property NSTextView *detail;
@property NSScrollView *fileScroll, *detailScroll;
@property NSStackView *queueHeader, *actions, *dropContents;
@property NSTextField *formats;
@property NSImageView *dropIcon;
@property NSLayoutConstraint *compactDropHeight;
@property NSProgressIndicator *progress;
@property FileDropView *drop;
@property FileDropView *windowDrop;
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
    NSMenuItem *reveal=[fileMenu addItemWithTitle:@"Показать выбранный файл в Finder" action:@selector(revealFile:) keyEquivalent:@"r"]; reveal.target=self;
    NSMenuItem *remove=[fileMenu addItemWithTitle:@"Убрать выбранные из списка" action:@selector(removeFiles:) keyEquivalent:@"\b"]; remove.target=self;
    [fileMenu addItemWithTitle:@"Закрыть окно" action:@selector(performClose:) keyEquivalent:@"w"]; file.submenu=fileMenu;
    NSMenuItem *edit=[NSMenuItem new]; [menu addItem:edit]; NSMenu *editMenu=[[NSMenu alloc] initWithTitle:@"Правка"];
    [editMenu addItemWithTitle:@"Копировать" action:@selector(copy:) keyEquivalent:@"c"];
    [editMenu addItemWithTitle:@"Вставить" action:@selector(paste:) keyEquivalent:@"v"];
    [editMenu addItemWithTitle:@"Выбрать всё" action:@selector(selectAll:) keyEquivalent:@"a"]; edit.submenu=editMenu;
    NSApp.mainMenu=menu;
}
- (NSImageView *)symbol:(NSString *)name size:(CGFloat)size {
    NSImageView *image=[NSImageView new];
    image.image=[[NSImage imageWithSystemSymbolName:name accessibilityDescription:nil] imageWithSymbolConfiguration:[NSImageSymbolConfiguration configurationWithPointSize:size weight:NSFontWeightRegular]];
    image.contentTintColor=NSColor.secondaryLabelColor;
    [image.widthAnchor constraintEqualToConstant:size+8].active=YES;
    [image.heightAnchor constraintEqualToConstant:size+8].active=YES;
    return image;
}
- (void)buildWindow {
    self.window=[[NSWindow alloc] initWithContentRect:NSMakeRect(0,0,800,700) styleMask:NSWindowStyleMaskTitled|NSWindowStyleMaskClosable|NSWindowStyleMaskMiniaturizable|NSWindowStyleMaskResizable backing:NSBackingStoreBuffered defer:NO];
    self.window.title=@"MetadataDel"; self.window.minSize=NSMakeSize(760,680); self.window.delegate=self; [self.window center];
    self.window.backgroundColor=NSColor.windowBackgroundColor;
    self.windowDrop=[[FileDropView alloc] initWithFrame:self.window.contentView.bounds]; self.windowDrop.plainSurface=YES;
    self.window.contentView=self.windowDrop;
    NSImageView *icon=[NSImageView new]; icon.image=NSApp.applicationIconImage;
    [icon.widthAnchor constraintEqualToConstant:40].active=YES; [icon.heightAnchor constraintEqualToConstant:40].active=YES;
    NSTextField *subtitle=[self label:@"Подготовьте документы к отправке" size:13 weight:NSFontWeightRegular]; subtitle.textColor=NSColor.secondaryLabelColor;
    NSStackView *brand=[self stack:@[[self label:@"MetadataDel" size:23 weight:NSFontWeightSemibold],subtitle] vertical:YES]; brand.spacing=3;
    self.settings=[self button:@"Настройки" action:@selector(showSettings:)]; self.settings.controlSize=NSControlSizeRegular;
    NSStackView *header=[self stack:@[icon,brand,[self spacer],self.settings] vertical:NO];
    self.integrationStatus=[self label:@"Очистка правым кликом в Finder" size:12 weight:NSFontWeightMedium];
    self.integration=[self button:@"Включить" action:@selector(installIntegration:)]; self.integration.controlSize=NSControlSizeRegular;
    NSStackView *integrationRow=[self stack:@[[self symbol:@"cursorarrow.click" size:15],self.integrationStatus,[self spacer],self.integration] vertical:NO];
    [integrationRow.heightAnchor constraintEqualToConstant:30].active=YES;
    self.integrationNotice=[self label:@"" size:12 weight:NSFontWeightRegular]; self.integrationNotice.hidden=YES; self.integrationNotice.selectable=YES;
    self.drop=[[FileDropView alloc] initWithFrame:NSZeroRect]; self.drop.translatesAutoresizingMaskIntoConstraints=NO;
    self.choose=[self button:@"Выбрать файлы…" action:@selector(pick:)];
    self.dropIcon=[self symbol:@"doc.badge.arrow.up" size:36]; self.dropIcon.contentTintColor=NSColor.controlAccentColor;
    self.dropTitle=[self label:@"Перетащите документы сюда" size:23 weight:NSFontWeightSemibold];
    self.dropHint=[self label:@"Обработка на вашем компьютере. Файлы никуда не отправляются." size:12 weight:NSFontWeightRegular]; self.dropHint.textColor=NSColor.secondaryLabelColor;
    self.formats=[self label:@"PDF  ·  Word  ·  Excel" size:12 weight:NSFontWeightMedium]; self.formats.textColor=NSColor.secondaryLabelColor;
    NSStackView *dropContents=[self stack:@[self.dropIcon,self.dropTitle,self.dropHint,self.choose,self.formats] vertical:YES];
    self.dropContents=dropContents; dropContents.alignment=NSLayoutAttributeCenterX; dropContents.spacing=12; dropContents.translatesAutoresizingMaskIntoConstraints=NO; [self.drop addSubview:dropContents];
    [NSLayoutConstraint activateConstraints:@[[dropContents.centerXAnchor constraintEqualToAnchor:self.drop.centerXAnchor],[dropContents.centerYAnchor constraintEqualToAnchor:self.drop.centerYAnchor],[dropContents.widthAnchor constraintLessThanOrEqualToAnchor:self.drop.widthAnchor constant:-32]]];
    self.compactDropHeight=[self.drop.heightAnchor constraintEqualToConstant:80];
    [self.drop setContentHuggingPriority:1 forOrientation:NSLayoutConstraintOrientationVertical];
    __weak AppDelegate *weakSelf=self; self.drop.onFiles=^(NSArray<NSURL *> *urls){ [weakSelf addURLs:urls]; };
    self.windowDrop.onFiles=self.drop.onFiles;
    self.summary=[self label:@"" size:12 weight:NSFontWeightMedium];
    self.clearList=[self button:@"Очистить список" action:@selector(clearFiles:)]; self.clearList.controlSize=NSControlSizeRegular; self.clearList.toolTip=@"Убрать все строки. Файлы на диске не удаляются.";
    self.queueHeader=[self stack:@[self.summary,[self spacer],self.clearList] vertical:NO];
    self.table=[NSTableView new]; self.table.delegate=self; self.table.dataSource=self; self.table.rowHeight=56; self.table.allowsMultipleSelection=YES;
    self.table.style=NSTableViewStyleFullWidth; self.table.headerView=nil; self.table.usesAlternatingRowBackgroundColors=NO;
    self.table.backgroundColor=NSColor.controlBackgroundColor;
    NSTableColumn *name=[[NSTableColumn alloc] initWithIdentifier:@"name"]; name.width=435; name.minWidth=280; name.resizingMask=NSTableColumnAutoresizingMask;
    NSTableColumn *state=[[NSTableColumn alloc] initWithIdentifier:@"state"]; state.width=195; state.minWidth=195; state.maxWidth=195;
    [self.table addTableColumn:name]; [self.table addTableColumn:state]; self.table.columnAutoresizingStyle=NSTableViewFirstColumnOnlyAutoresizingStyle;
    self.table.doubleAction=@selector(revealFile:); self.table.target=self;
    self.fileScroll=[NSScrollView new]; self.fileScroll.documentView=self.table; self.fileScroll.hasVerticalScroller=YES; self.fileScroll.borderType=NSNoBorder;
    [self.fileScroll setContentHuggingPriority:1 forOrientation:NSLayoutConstraintOrientationVertical];
    self.remove=[self button:@"Убрать выбранные" action:@selector(removeFiles:)]; self.remove.toolTip=@"Убрать строки из списка. Файлы останутся на диске.";
    self.reveal=[self button:@"Показать в Finder" action:@selector(revealFile:)];
    self.remove.controlSize=self.reveal.controlSize=NSControlSizeRegular;
    self.actions=[self stack:@[self.remove,self.reveal,[self spacer]] vertical:NO];
    self.detail=[[NSTextView alloc] initWithFrame:NSMakeRect(0,0,700,50)]; self.detail.editable=NO; self.detail.selectable=YES;
    self.detail.drawsBackground=NO; self.detail.font=[NSFont systemFontOfSize:12]; self.detail.textColor=NSColor.secondaryLabelColor;
    self.detail.verticallyResizable=YES; self.detail.horizontallyResizable=NO; self.detail.autoresizingMask=NSViewWidthSizable;
    self.detail.textContainer.widthTracksTextView=YES; self.detail.textContainerInset=NSMakeSize(0,3);
    self.detailScroll=[NSScrollView new]; self.detailScroll.documentView=self.detail; self.detailScroll.drawsBackground=NO; self.detailScroll.hasVerticalScroller=YES;
    [self.detailScroll.heightAnchor constraintEqualToConstant:50].active=YES;
    self.backup=[NSButton checkboxWithTitle:@"Сохранять резервные копии" target:self action:@selector(backupChanged:)]; self.backup.state=NSControlStateValueOn;
    self.backupHint=[self label:@"Копии .bak — рядом. Оригиналы будут заменены." size:11 weight:NSFontWeightRegular]; self.backupHint.textColor=NSColor.secondaryLabelColor;
    NSStackView *options=[self stack:@[self.backup,self.backupHint] vertical:YES]; options.spacing=4;
    self.clean=[self button:@"Очистить файлы" action:@selector(cleanFiles:)]; self.clean.bezelColor=[NSColor colorWithSRGBRed:0.69 green:0.27 blue:0.14 alpha:1];
    self.clean.keyEquivalent=@"\r"; self.clean.keyEquivalentModifierMask=NSEventModifierFlagCommand;
    self.clean.toolTip=@"Очистить готовые файлы · ⌘↵";
    NSStackView *footer=[self stack:@[options,[self spacer],self.clean] vertical:NO]; [footer.heightAnchor constraintEqualToConstant:58].active=YES;
    self.progress=[NSProgressIndicator new]; self.progress.indeterminate=NO; self.progress.style=NSProgressIndicatorStyleBar;
    NSBox *divider=[NSBox new]; divider.boxType=NSBoxSeparator;
    NSStackView *root=[self stack:@[header,integrationRow,self.integrationNotice,self.drop,self.queueHeader,self.fileScroll,self.actions,self.detailScroll,self.progress,divider,footer] vertical:YES];
    root.translatesAutoresizingMaskIntoConstraints=NO; root.spacing=14; [self.window.contentView addSubview:root];
    [NSLayoutConstraint activateConstraints:@[[root.leadingAnchor constraintEqualToAnchor:self.window.contentView.leadingAnchor constant:28],[root.trailingAnchor constraintEqualToAnchor:self.window.contentView.trailingAnchor constant:-28],[root.topAnchor constraintEqualToAnchor:self.window.contentView.topAnchor constant:22],[root.bottomAnchor constraintEqualToAnchor:self.window.contentView.bottomAnchor constant:-18]]];
    for (NSView *view in root.arrangedSubviews) [view.widthAnchor constraintEqualToAnchor:root.widthAnchor].active=YES;
    [self refresh];
}
- (void)addURLs:(NSArray *)urls {
    if (self.busy) return;
    NSMutableSet *known=[NSMutableSet new]; for (NSDictionary *file in self.files) [known addObject:file[@"path"]];
    for (id input in urls) {
        NSURL *url=[input isKindOfClass:NSURL.class] ? input : [NSURL fileURLWithPath:input]; if (!url.isFileURL) continue;
        NSString *path=url.path.stringByStandardizingPath; if ([known containsObject:path]) continue; [known addObject:path];
        BOOL directory=NO; [NSFileManager.defaultManager fileExistsAtPath:path isDirectory:&directory];
        BOOL supported=[@[@"pdf",@"docx",@"xlsx",@"doc",@"xls"] containsObject:path.pathExtension.lowercaseString];
        NSString *state=directory ? @"Папка пропущена" : supported ? @"Готов к очистке" : @"Другой формат";
        [self.files addObject:[@{@"path":path,@"state":state,@"attempted":@(directory || !supported),@"kind":directory || !supported ? @"unsupported" : @"pending",@"message":directory ? @"Папки обрабатываются через правый клик в Finder." : supported ? path : @"Поддерживаются PDF, DOCX, XLSX, DOC и XLS. Этот файл не будет изменён."} mutableCopy]];
    }
    [self.table reloadData]; [self refresh]; [self updateDetail];
}
- (NSInteger)numberOfRowsInTableView:(NSTableView *)tableView { return self.files.count; }
- (NSView *)tableView:(NSTableView *)tableView viewForTableColumn:(NSTableColumn *)column row:(NSInteger)row {
    NSDictionary *file=self.files[row]; BOOL name=[column.identifier isEqual:@"name"];
    StatusCellView *cell=[StatusCellView new]; cell.toolTip=[NSString stringWithFormat:@"%@\n%@",file[@"path"],file[@"message"]];
    NSView *content;
    if (name) {
        NSImageView *icon=[NSImageView new]; icon.image=[NSWorkspace.sharedWorkspace iconForFile:file[@"path"]];
        [icon.widthAnchor constraintEqualToConstant:28].active=YES; [icon.heightAnchor constraintEqualToConstant:32].active=YES;
        NSTextField *title=[NSTextField labelWithString:[file[@"path"] lastPathComponent]]; title.font=[NSFont systemFontOfSize:13 weight:NSFontWeightMedium]; title.lineBreakMode=NSLineBreakByTruncatingMiddle;
        NSTextField *folder=[NSTextField labelWithString:[[file[@"path"] stringByDeletingLastPathComponent] stringByAbbreviatingWithTildeInPath]]; folder.font=[NSFont systemFontOfSize:11]; folder.textColor=NSColor.secondaryLabelColor; folder.lineBreakMode=NSLineBreakByTruncatingMiddle;
        NSStackView *labels=[self stack:@[title,folder] vertical:YES]; labels.spacing=3;
        [title.widthAnchor constraintEqualToAnchor:labels.widthAnchor].active=YES; [folder.widthAnchor constraintEqualToAnchor:labels.widthAnchor].active=YES;
        content=[self stack:@[icon,labels] vertical:NO];
    } else {
        NSString *kind=file[@"kind"] ?: @"pending";
        NSDictionary *symbols=@{@"pending":@"clock",@"processing":@"arrow.triangle.2.circlepath",@"success":@"checkmark.circle.fill",@"warning":@"exclamationmark.triangle.fill",@"error":@"xmark.circle.fill",@"unsupported":@"minus.circle"};
        NSColor *color=[kind isEqual:@"success"] ? NSColor.systemGreenColor : [kind isEqual:@"error"] ? NSColor.systemRedColor : [kind isEqual:@"warning"] ? NSColor.systemOrangeColor : NSColor.secondaryLabelColor;
        NSImageView *icon=[self symbol:symbols[kind] size:13]; icon.contentTintColor=color;
        NSTextField *text=[NSTextField labelWithString:file[@"state"]]; text.font=[NSFont systemFontOfSize:12]; text.textColor=color; text.lineBreakMode=NSLineBreakByTruncatingTail;
        cell.statusColor=color; cell.statusLabel=text; cell.statusIcon=icon;
        content=[self stack:@[icon,text] vertical:NO]; ((NSStackView *)content).spacing=5;
    }
    content.translatesAutoresizingMaskIntoConstraints=NO; [cell addSubview:content];
    [NSLayoutConstraint activateConstraints:@[[content.leadingAnchor constraintEqualToAnchor:cell.leadingAnchor constant:8],[content.trailingAnchor constraintEqualToAnchor:cell.trailingAnchor constant:-8],[content.centerYAnchor constraintEqualToAnchor:cell.centerYAnchor]]];
    return cell;
}
- (void)updateDetail {
    NSInteger row=self.table.selectedRow;
    if (row>=0 && row<self.files.count && self.table.selectedRowIndexes.count==1) {
        NSDictionary *file=self.files[row];
        self.detail.string=[NSString stringWithFormat:@"%@ — %@\n%@",[file[@"path"] lastPathComponent],file[@"state"],file[@"message"]];
    } else self.detail.string=self.table.selectedRowIndexes.count>1 ? @"Выбрано несколько документов. «Убрать выбранные» удаляет только строки из списка." : @"Выберите документ, чтобы увидеть путь и подробности результата.";
    [self.detail scrollRangeToVisible:NSMakeRange(0,0)];
}
- (void)tableViewSelectionDidChange:(NSNotification *)notification { [self refresh]; [self updateDetail]; }
- (void)refresh {
    NSUInteger pending=0,ok=0,warn=0,failed=0,skipped=0;
    for (NSDictionary *file in self.files) {
        NSString *kind=file[@"kind"];
        if (![file[@"attempted"] boolValue]) pending++;
        else if ([kind isEqual:@"success"]) ok++;
        else if ([kind isEqual:@"warning"]) { ok++; warn++; }
        else if ([kind isEqual:@"error"]) failed++;
        else if ([kind isEqual:@"unsupported"]) skipped++;
    }
    BOOL hasFiles=self.files.count>0;
    self.queueHeader.hidden=self.fileScroll.hidden=self.actions.hidden=self.detailScroll.hidden=!hasFiles;
    self.progress.hidden=!self.busy || !hasFiles;
    self.compactDropHeight.active=hasFiles;
    self.dropIcon.hidden=self.dropHint.hidden=self.formats.hidden=hasFiles;
    self.dropContents.orientation=hasFiles ? NSUserInterfaceLayoutOrientationHorizontal : NSUserInterfaceLayoutOrientationVertical;
    self.dropContents.alignment=hasFiles ? NSLayoutAttributeCenterY : NSLayoutAttributeCenterX;
    self.dropTitle.stringValue=self.busy ? @"Дождитесь завершения обработки" : hasFiles ? @"Добавьте ещё документы" : @"Перетащите документы сюда";
    self.dropTitle.font=[NSFont systemFontOfSize:hasFiles ? 15 : 23 weight:NSFontWeightSemibold];
    self.choose.controlSize=hasFiles ? NSControlSizeRegular : NSControlSizeLarge;
    self.clean.enabled=!self.busy && pending>0; self.clean.title=self.busy ? @"Обработка…" : pending ? [NSString stringWithFormat:@"Очистить · %lu",pending] : @"Очистить файлы";
    self.choose.enabled=self.backup.enabled=self.settings.enabled=self.integration.enabled=self.drop.enabled=!self.busy;
    self.windowDrop.enabled=!self.busy;
    self.clearList.enabled=!self.busy && hasFiles;
    self.remove.enabled=!self.busy && self.table.selectedRowIndexes.count>0; self.reveal.enabled=!self.busy && self.table.selectedRowIndexes.count==1;
    BOOL installed=[IntegrationManager isInstalled],stale=[IntegrationManager needsUpdate];
    self.integrationStatus.stringValue=installed ? @"Правый клик в Finder включён" : @"Очистка правым кликом в Finder";
    self.integration.title=installed ? @"Настроить" : stale ? @"Восстановить" : @"Включить";
    self.integration.action=installed ? @selector(showSettings:) : @selector(installIntegration:);
    if (!self.busy) {
        NSMutableArray *parts=[NSMutableArray new];
        if (pending) [parts addObject:[NSString stringWithFormat:@"Готово к очистке: %lu",pending]];
        if (ok) [parts addObject:[NSString stringWithFormat:@"Очищено: %lu",ok]];
        if (warn) [parts addObject:[NSString stringWithFormat:@"С замечаниями: %lu",warn]];
        if (failed) [parts addObject:[NSString stringWithFormat:@"Не обработано: %lu",failed]];
        if (skipped) [parts addObject:[NSString stringWithFormat:@"Пропущено: %lu",skipped]];
        self.summary.stringValue=[parts componentsJoinedByString:@" · "];
        if (!hasFiles) { self.progress.doubleValue=0; self.detail.string=@""; }
    }
}
- (void)backupChanged:(id)sender {
    BOOL on=self.backup.state==NSControlStateValueOn;
    self.backupHint.stringValue=on ? @"Копии .bak — рядом. Оригиналы будут заменены." : @"Без резервных копий. Оригиналы будут заменены.";
    self.backupHint.textColor=on ? NSColor.secondaryLabelColor : NSColor.systemOrangeColor;
}
- (void)clearFiles:(id)sender { if (self.busy) return; [self.files removeAllObjects]; [self.table reloadData]; [self refresh]; }
- (void)pick:(id)sender {
    if (self.busy) return;
    NSOpenPanel *panel=[NSOpenPanel openPanel]; panel.canChooseFiles=YES; panel.canChooseDirectories=NO; panel.allowsMultipleSelection=YES;
    panel.message=@"Выберите PDF, Word или Excel-документы"; NSMutableArray *types=[NSMutableArray new]; for (NSString *ext in @[@"pdf",@"docx",@"xlsx",@"doc",@"xls"]) { UTType *type=[UTType typeWithFilenameExtension:ext]; if (type) [types addObject:type]; } panel.allowedContentTypes=types; panel.allowsOtherFileTypes=NO;
    [panel beginSheetModalForWindow:self.window completionHandler:^(NSModalResponse response){ if (response==NSModalResponseOK) [self addURLs:panel.URLs]; }];
}
- (void)removeFiles:(id)sender { if (!self.busy) { [self.files removeObjectsAtIndexes:self.table.selectedRowIndexes]; [self.table reloadData]; [self refresh]; [self updateDetail]; } }
- (void)revealFile:(id)sender {
    NSInteger row=self.table.selectedRow; if (self.busy || row<0 || row>=self.files.count || self.table.selectedRowIndexes.count!=1) return; NSDictionary *file=self.files[row];
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
        self.busy=NO; [self refresh];
        if (self.table.selectedRow<0 && self.files.count) {
            NSUInteger row=[self.files indexOfObjectPassingTest:^BOOL(NSDictionary *file,NSUInteger index,BOOL *stop){ return [file[@"kind"] isEqual:@"error"]; }];
            [self.table selectRowIndexes:[NSIndexSet indexSetWithIndex:row==NSNotFound ? 0 : row] byExtendingSelection:NO];
        }
        [self updateDetail];
        if (self.closeAfterWork) [NSApp terminate:nil]; return;
    }
    next[@"attempted"]=@YES; next[@"state"]=@"Обрабатывается…"; next[@"kind"]=@"processing";
    self.summary.stringValue=[NSString stringWithFormat:@"Обработка %lu из %.0f",self.completed+1,self.progress.maxValue]; [self.table reloadData];
    [self.runner cleanFileURL:[NSURL fileURLWithPath:next[@"path"]] backup:self.backup.state==NSControlStateValueOn completion:^(NSDictionary *result,NSError *error){
        BOOL success=!error && [result[@"success"] boolValue]; NSString *message=[result[@"message"] isKindOfClass:NSString.class] ? result[@"message"] : nil;
        BOOL warning=success && message.length>0; if (success) self.successes++; if (warning) self.warnings++;
        next[@"state"]=success ? warning ? @"Есть замечания" : @"Очищен" : @"Не обработан";
        next[@"kind"]=success ? warning ? @"warning" : @"success" : @"error";
        if (success) next[@"output"]=result[@"outputPath"];
        next[@"message"]=error.localizedDescription ?: message ?: result[@"outputPath"] ?: @"Не удалось обработать файл.";
        [self updateDetail]; self.progress.doubleValue=++self.completed; [self.table reloadData]; [self cleanNext];
    }];
}
- (void)installIntegration:(id)sender {
    if (self.busy) return; self.busy=YES; [self refresh]; self.integrationNotice.hidden=NO; self.integrationNotice.stringValue=@"Настраиваю очистку правым кликом…";
    [IntegrationManager installWithCompletion:^(NSError *error){ self.busy=NO; [self refresh]; self.integrationNotice.stringValue=error.localizedDescription ?: @"Готово. Finder → Быстрые действия → Удалить метаданные (MetadataDel)."; if (self.closeAfterWork) [NSApp terminate:nil]; }];
}
- (void)showSettings:(id)sender {
    if (self.busy) return;
    NSAlert *alert=[NSAlert new]; alert.messageText=@"MetadataDel · Настройки";
    alert.informativeText=@"Правый клик в Finder работает без открытого окна. Резервные копии для него всегда включены.\n\nЗдесь можно восстановить интеграцию или удалить команды и действие Finder. Документы и их копии сохранятся. Для удаления самого приложения переместите его в Корзину.";
    [alert addButtonWithTitle:@"Готово"]; [alert addButtonWithTitle:@"Восстановить Finder"]; [alert addButtonWithTitle:@"Удалить интеграцию"];
    [alert beginSheetModalForWindow:self.window completionHandler:^(NSModalResponse response){
        if (response==NSAlertSecondButtonReturn) [self installIntegration:nil];
        else if (response==NSAlertThirdButtonReturn) {
            self.busy=YES; [self refresh]; self.integrationNotice.hidden=NO; self.integrationNotice.stringValue=@"Удаляю интеграцию Finder…"; [IntegrationManager removeWithCompletion:^(NSError *error){ self.busy=NO; [self refresh]; self.integrationNotice.stringValue=error.localizedDescription ?: @"Действие Finder и команды удалены. Документы сохранены."; if (self.closeAfterWork) [NSApp terminate:nil]; }];
        }
    }];
}
- (BOOL)validateMenuItem:(NSMenuItem *)item {
    if (item.action==@selector(pick:)) return !self.busy;
    if (item.action==@selector(removeFiles:)) return self.remove.enabled;
    if (item.action==@selector(revealFile:)) return self.reveal.enabled;
    return YES;
}
- (BOOL)windowShouldClose:(NSWindow *)sender { if (self.busy) { self.closeAfterWork=YES; self.detail.string=@"Окно закроется после завершения обработки."; return NO; } return YES; }
- (NSApplicationTerminateReply)applicationShouldTerminate:(NSApplication *)sender { return [self windowShouldClose:self.window] ? NSTerminateNow : NSTerminateCancel; }
- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)sender { return YES; }
- (void)application:(NSApplication *)sender openFiles:(NSArray<NSString *> *)filenames {
    if (self.busy) { self.detail.string=@"Дождитесь завершения обработки и добавьте новые файлы ещё раз."; [sender replyToOpenOrPrint:NSApplicationDelegateReplyFailure]; return; }
    if (!self.files) self.initialFiles=filenames; else [self addURLs:filenames]; [sender replyToOpenOrPrint:NSApplicationDelegateReplySuccess];
}
@end
int main(int argc,const char **argv) { @autoreleasepool { NSApplication *app=NSApplication.sharedApplication; AppDelegate *delegate=[AppDelegate new]; app.delegate=delegate; [app run]; } return 0; }
