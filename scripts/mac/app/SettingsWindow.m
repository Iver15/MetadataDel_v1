#import "SettingsWindow.h"
#import "IntegrationManager.h"

NSString *const MDBackupDefaultsKey=@"BackupEnabled";

@implementation SettingsWindow
- (NSTextField *)label:(NSString *)text size:(CGFloat)size weight:(NSFontWeight)weight secondary:(BOOL)secondary {
    NSTextField *label=[NSTextField wrappingLabelWithString:text]; label.font=[NSFont systemFontOfSize:size weight:weight];
    label.textColor=secondary ? NSColor.secondaryLabelColor : NSColor.labelColor; label.selectable=NO; return label;
}
- (NSStackView *)stack:(NSArray<NSView *> *)views vertical:(BOOL)vertical spacing:(CGFloat)spacing {
    NSStackView *stack=[NSStackView stackViewWithViews:views]; stack.orientation=vertical ? NSUserInterfaceLayoutOrientationVertical : NSUserInterfaceLayoutOrientationHorizontal;
    stack.alignment=vertical ? NSLayoutAttributeLeading : NSLayoutAttributeCenterY; stack.spacing=spacing; return stack;
}
- (NSImageView *)symbol:(NSString *)name size:(CGFloat)size {
    NSImageView *image=[NSImageView new];
    image.image=[[NSImage imageWithSystemSymbolName:name accessibilityDescription:nil] imageWithSymbolConfiguration:[NSImageSymbolConfiguration configurationWithPointSize:size weight:NSFontWeightMedium]];
    [image.widthAnchor constraintEqualToConstant:size+6].active=YES; [image.heightAnchor constraintEqualToConstant:size+6].active=YES;
    return image;
}
// A titled group: a quiet caption above a rounded surface, like System Settings.
- (NSView *)section:(NSString *)title content:(NSView *)content {
    NSBox *box=[NSBox new]; box.boxType=NSBoxCustom; box.cornerRadius=10; box.borderWidth=1;
    box.borderColor=NSColor.separatorColor; box.fillColor=[NSColor.labelColor colorWithAlphaComponent:0.035]; box.titlePosition=NSNoTitle;
    box.contentViewMargins=NSZeroSize; box.translatesAutoresizingMaskIntoConstraints=NO;
    content.translatesAutoresizingMaskIntoConstraints=NO; [box addSubview:content];
    [NSLayoutConstraint activateConstraints:@[[content.leadingAnchor constraintEqualToAnchor:box.leadingAnchor constant:14],[content.trailingAnchor constraintEqualToAnchor:box.trailingAnchor constant:-14],[content.topAnchor constraintEqualToAnchor:box.topAnchor constant:12],[content.bottomAnchor constraintEqualToAnchor:box.bottomAnchor constant:-12]]];
    NSTextField *caption=[self label:title size:12 weight:NSFontWeightSemibold secondary:YES];
    NSStackView *group=[self stack:@[caption,box] vertical:YES spacing:6];
    [box.widthAnchor constraintEqualToAnchor:group.widthAnchor].active=YES;
    return group;
}
- (instancetype)init {
    NSWindow *window=[[NSWindow alloc] initWithContentRect:NSMakeRect(0,0,500,300) styleMask:NSWindowStyleMaskTitled|NSWindowStyleMaskClosable backing:NSBackingStoreBuffered defer:YES];
    window.title=@"Настройки"; window.releasedWhenClosed=NO; [window standardWindowButton:NSWindowZoomButton].enabled=NO;
    if (!(self=[super initWithWindow:window])) return nil;

    NSImageView *icon=[NSImageView new]; icon.image=NSApp.applicationIconImage;
    [icon.widthAnchor constraintEqualToConstant:44].active=YES; [icon.heightAnchor constraintEqualToConstant:44].active=YES;
    NSString *version=NSBundle.mainBundle.infoDictionary[@"CFBundleShortVersionString"] ?: @"—";
    NSStackView *brand=[self stack:@[[self label:@"MetadataDel" size:17 weight:NSFontWeightSemibold secondary:NO],[self label:[NSString stringWithFormat:@"Версия %@ · обработка только на этом Mac",version] size:12 weight:NSFontWeightRegular secondary:YES]] vertical:YES spacing:2];
    NSStackView *header=[self stack:@[icon,brand] vertical:NO spacing:12];

    self.statusIcon=[self symbol:@"circle.dashed" size:15];
    self.status=[self label:@"" size:13 weight:NSFontWeightMedium secondary:NO];
    self.statusHint=[self label:@"" size:12 weight:NSFontWeightRegular secondary:YES];
    NSStackView *statusText=[self stack:@[self.status,self.statusHint] vertical:YES spacing:3];
    NSStackView *statusRow=[self stack:@[self.statusIcon,statusText] vertical:NO spacing:8]; statusRow.alignment=NSLayoutAttributeTop;
    self.install=[NSButton buttonWithTitle:@"Включить" target:self action:@selector(installClicked:)];
    self.remove=[NSButton buttonWithTitle:@"Отключить…" target:self action:@selector(removeClicked:)];
    NSView *gap=[NSView new]; [gap setContentHuggingPriority:1 forOrientation:NSLayoutConstraintOrientationHorizontal];
    NSTextField *finderNote=[self label:@"Из Finder резервные копии создаются всегда." size:11 weight:NSFontWeightRegular secondary:YES];
    NSStackView *buttons=[self stack:@[finderNote,gap,self.remove,self.install] vertical:NO spacing:8];
    NSStackView *finder=[self stack:@[statusRow,buttons] vertical:YES spacing:12];
    [buttons.widthAnchor constraintEqualToAnchor:finder.widthAnchor].active=YES;
    [statusText.widthAnchor constraintLessThanOrEqualToConstant:400].active=YES;

    self.backup=[NSButton checkboxWithTitle:@"Сохранять резервные копии" target:self action:@selector(backupClicked:)];
    NSTextField *backupHint=[self label:@"Копия «имя.bak» появляется рядом с оригиналом. Флажок в главном окне меняет эту же настройку." size:11 weight:NSFontWeightRegular secondary:YES];
    NSStackView *window_=[self stack:@[self.backup,backupHint] vertical:YES spacing:4];
    [backupHint.widthAnchor constraintEqualToAnchor:window_.widthAnchor constant:-20].active=YES;
    [backupHint setContentCompressionResistancePriority:NSLayoutPriorityRequired forOrientation:NSLayoutConstraintOrientationVertical];

    NSTextField *logsHint=[NSTextField labelWithString:@"Журнал очистки из Finder: ~/Library/Logs/MetadataDel"];
    logsHint.font=[NSFont systemFontOfSize:12]; logsHint.textColor=NSColor.secondaryLabelColor; logsHint.lineBreakMode=NSLineBreakByTruncatingMiddle;
    [logsHint setContentCompressionResistancePriority:NSLayoutPriorityDefaultLow forOrientation:NSLayoutConstraintOrientationHorizontal];
    NSButton *logs=[NSButton buttonWithTitle:@"Открыть" target:self action:@selector(openLogs:)];
    NSView *gap2=[NSView new]; [gap2 setContentHuggingPriority:1 forOrientation:NSLayoutConstraintOrientationHorizontal];
    NSStackView *logRow=[self stack:@[logsHint,gap2,logs] vertical:NO spacing:8];

    NSTextField *footer=[self label:@"Чтобы удалить MetadataDel, отключите правый клик и переместите приложение в Корзину. Документы и их копии не затрагиваются." size:11 weight:NSFontWeightRegular secondary:YES];
    NSStackView *root=[self stack:@[header,[self section:@"Правый клик в Finder" content:finder],[self section:@"Главное окно" content:window_],[self section:@"Журнал" content:logRow],footer] vertical:YES spacing:18];
    [root setCustomSpacing:22 afterView:header];
    root.translatesAutoresizingMaskIntoConstraints=NO; [window.contentView addSubview:root];
    [NSLayoutConstraint activateConstraints:@[[root.leadingAnchor constraintEqualToAnchor:window.contentView.leadingAnchor constant:24],[root.trailingAnchor constraintEqualToAnchor:window.contentView.trailingAnchor constant:-24],[root.topAnchor constraintEqualToAnchor:window.contentView.topAnchor constant:20],[root.bottomAnchor constraintEqualToAnchor:window.contentView.bottomAnchor constant:-20],[root.widthAnchor constraintEqualToConstant:452]]];
    for (NSView *view in root.arrangedSubviews) if (view!=header) [view.widthAnchor constraintEqualToAnchor:root.widthAnchor].active=YES;
    [self refreshBusy:NO notice:nil];
    [window center];
    return self;
}
- (void)refreshBusy:(BOOL)busy notice:(NSString *)notice {
    MDIntegrationState state=[IntegrationManager state];
    NSArray *titles=@[@"Выключен",@"Включён",@"Нужно обновить",@"Недоступен из этой копии"];
    NSArray *hints=@[@"Включите, чтобы очищать файлы и папки правым кликом: Finder → Быстрые действия.",
                     @"Finder → Быстрые действия → «Удалить метаданные (MetadataDel)». Работает без открытого окна.",
                     @"Действие Finder установлено другой версией приложения. Обновите его.",
                     @"Скопируйте MetadataDel в папку «Программы» и откройте приложение оттуда."];
    NSArray *symbols=@[@"circle.dashed",@"checkmark.circle.fill",@"exclamationmark.triangle.fill",@"info.circle"];
    NSArray *colors=@[NSColor.secondaryLabelColor,NSColor.systemGreenColor,NSColor.systemOrangeColor,NSColor.secondaryLabelColor];
    self.status.stringValue=titles[state]; self.statusHint.stringValue=notice.length ? notice : hints[state];
    self.statusIcon.image=[[NSImage imageWithSystemSymbolName:symbols[state] accessibilityDescription:titles[state]] imageWithSymbolConfiguration:[NSImageSymbolConfiguration configurationWithPointSize:15 weight:NSFontWeightMedium]];
    self.statusIcon.contentTintColor=colors[state];
    self.install.title=state==MDIntegrationOn ? @"Восстановить" : state==MDIntegrationStale ? @"Обновить" : @"Включить";
    self.install.keyEquivalent=state==MDIntegrationOn ? @"" : @"\r";
    self.install.enabled=!busy && state!=MDIntegrationOutsideApplications;
    self.remove.enabled=!busy && ([IntegrationManager isInstalled] || [IntegrationManager needsUpdate]);
    self.backup.enabled=!busy;
    self.backup.state=[NSUserDefaults.standardUserDefaults boolForKey:MDBackupDefaultsKey] ? NSControlStateValueOn : NSControlStateValueOff;
}
- (void)installClicked:(id)sender { if (self.onInstall) self.onInstall(); }
- (void)removeClicked:(id)sender {
    NSAlert *alert=[NSAlert new]; alert.alertStyle=NSAlertStyleWarning;
    alert.messageText=@"Отключить очистку правым кликом?";
    alert.informativeText=@"Действие Finder и команда metadatadel будут удалены. Документы, резервные копии и само приложение останутся на месте. Включить снова можно здесь же.";
    [alert addButtonWithTitle:@"Отключить"]; [alert addButtonWithTitle:@"Отмена"];
    alert.buttons[0].hasDestructiveAction=YES;
    [alert beginSheetModalForWindow:self.window completionHandler:^(NSModalResponse response){ if (response==NSAlertFirstButtonReturn && self.onRemove) self.onRemove(); }];
}
- (void)backupClicked:(id)sender {
    BOOL on=self.backup.state==NSControlStateValueOn; [NSUserDefaults.standardUserDefaults setBool:on forKey:MDBackupDefaultsKey];
    if (self.onBackupChanged) self.onBackupChanged(on);
}
- (void)openLogs:(id)sender {
    NSString *logs=[NSHomeDirectory() stringByAppendingPathComponent:@"Library/Logs/MetadataDel"];
    [NSFileManager.defaultManager createDirectoryAtPath:logs withIntermediateDirectories:YES attributes:nil error:nil];
    [NSWorkspace.sharedWorkspace openURL:[NSURL fileURLWithPath:logs isDirectory:YES]];
}
@end
