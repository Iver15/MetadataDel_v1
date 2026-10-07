#import "IntegrationManager.h"
@implementation IntegrationManager
+ (NSString *)support { return [NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/MetadataDel"]; }
// Resolve a complete installation from its home directory; also usable by diagnostics.
+ (BOOL)isInstalledAtHome:(NSString *)home {
    NSString *support=[home stringByAppendingPathComponent:@"Library/Application Support/MetadataDel"];
    NSString *workflow=[home stringByAppendingPathComponent:@"Library/Services/Удалить метаданные (MetadataDel).workflow/Contents"];
    NSString *exe=[home stringByAppendingPathComponent:@".local/bin/metadatadel"];
    NSString *script=[support stringByAppendingPathComponent:@"finder-action.sh"];
    NSString *config=[support stringByAppendingPathComponent:@"command-path"];
    NSFileManager *fm=NSFileManager.defaultManager;
    for (NSString *path in @[exe,script,config,[workflow stringByAppendingPathComponent:@"Info.plist"],[workflow stringByAppendingPathComponent:@"document.wflow"]]) {
        NSDictionary *attributes=[fm attributesOfItemAtPath:path error:nil];
        if (![attributes[NSFileType] isEqual:NSFileTypeRegular]) return NO;
    }
    NSString *configured=[[NSString stringWithContentsOfFile:config encoding:NSUTF8StringEncoding error:nil] stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    NSDictionary *info=[NSDictionary dictionaryWithContentsOfFile:[workflow stringByAppendingPathComponent:@"Info.plist"]];
    NSArray *services=info[@"NSServices"];
    BOOL owned=[services isKindOfClass:NSArray.class] && services.count>0 && [services[0] isKindOfClass:NSDictionary.class]
        && [services[0][@"NSMenuItem"] isKindOfClass:NSDictionary.class] && [services[0][@"NSMenuItem"][@"default"] isEqual:@"Удалить метаданные (MetadataDel)"];
    return owned && [fm isExecutableFileAtPath:exe] && [fm isExecutableFileAtPath:script] && [configured isEqual:exe]
        && [NSDictionary dictionaryWithContentsOfFile:[workflow stringByAppendingPathComponent:@"document.wflow"]] != nil;
}
+ (BOOL)isInstalled { return [self isInstalledAtHome:NSHomeDirectory()]; }
+ (BOOL)needsUpdate {
    BOOL existing=[NSFileManager.defaultManager fileExistsAtPath:[NSHomeDirectory() stringByAppendingPathComponent:@"Library/Services/Удалить метаданные (MetadataDel).workflow"]]
        || [NSFileManager.defaultManager fileExistsAtPath:[[self support] stringByAppendingPathComponent:@"desktop-version"]];
    NSString *version=[NSString stringWithContentsOfFile:[[self support] stringByAppendingPathComponent:@"desktop-version"] encoding:NSUTF8StringEncoding error:nil];
    return existing && (![self isInstalled] || ![[version stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet] isEqual:NSBundle.mainBundle.infoDictionary[@"CFBundleShortVersionString"]]);
}
+ (void)runScript:(NSString *)name args:(NSArray *)args completion:(void (^)(NSError *))completion {
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0), ^{
        NSTask *task=[NSTask new]; task.executableURL=[NSURL fileURLWithPath:@"/bin/bash"];
        NSString *script=[NSBundle.mainBundle.resourcePath stringByAppendingPathComponent:[@"scripts/mac/" stringByAppendingString:name]];
        task.arguments=[@[script] arrayByAddingObjectsFromArray:args];
        NSMutableDictionary *env=[NSProcessInfo.processInfo.environment mutableCopy]; env[@"HOME"]=NSHomeDirectory(); task.environment=env;
        NSPipe *pipe=[NSPipe pipe]; task.standardOutput=pipe; task.standardError=pipe;
        NSError *error=nil;
        if ([task launchAndReturnError:&error]) {
            NSData *data=[pipe.fileHandleForReading readDataToEndOfFile]; [task waitUntilExit];
            if (task.terminationStatus != 0) error=[NSError errorWithDomain:@"MetadataDel" code:task.terminationStatus userInfo:@{NSLocalizedDescriptionKey:[[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] ?: @"Не удалось настроить Finder."}];
        }
        dispatch_async(dispatch_get_main_queue(), ^{ completion(error); });
    });
}
+ (void)installWithCompletion:(void (^)(NSError *))completion {
    NSString *path=NSBundle.mainBundle.bundlePath;
    if (![path hasPrefix:@"/Applications/"] && ![path hasPrefix:[NSHomeDirectory() stringByAppendingString:@"/Applications/"]]) {
        completion([NSError errorWithDomain:@"MetadataDel" code:2 userInfo:@{NSLocalizedDescriptionKey:@"Сначала скопируйте MetadataDel в папку «Программы» и откройте приложение оттуда."}]); return;
    }
    [self runScript:@"install-app-integration.sh" args:@[NSBundle.mainBundle.resourcePath, NSBundle.mainBundle.infoDictionary[@"CFBundleShortVersionString"]] completion:completion];
}
+ (void)removeWithCompletion:(void (^)(NSError *))completion { [self runScript:@"uninstall.sh" args:@[] completion:completion]; }
@end
