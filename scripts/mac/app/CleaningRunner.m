#import "CleaningRunner.h"
@interface CleaningRunner ()
@property NSURL *executableURL;
@property(atomic) NSTask *currentTask;
@end
static BOOL MDIsBoolean(id value) { return [value isKindOfClass:NSNumber.class] && CFGetTypeID((__bridge CFTypeRef)value)==CFBooleanGetTypeID(); }
static BOOL MDIsOptionalString(id value) { return !value || value==NSNull.null || [value isKindOfClass:NSString.class]; }
@implementation CleaningRunner
- (instancetype)initWithExecutableURL:(NSURL *)url { if ((self=[super init])) _executableURL=url; return self; }
- (void)cleanFileURL:(NSURL *)url backup:(BOOL)backup completion:(void (^)(NSDictionary *, NSError *))completion {
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
        NSFileManager *fm=NSFileManager.defaultManager;
        NSString *dir=[NSTemporaryDirectory() stringByAppendingPathComponent:NSUUID.UUID.UUIDString];
        NSError *error=nil; NSDictionary *result=nil;
        [fm createDirectoryAtPath:dir withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions:@0700} error:&error];
        NSString *out=[dir stringByAppendingPathComponent:@"stdout"], *err=[dir stringByAppendingPathComponent:@"stderr"];
        NSFileHandle *outHandle=nil, *errHandle=nil;
        if (!error) {
            [fm createFileAtPath:out contents:nil attributes:@{NSFilePosixPermissions:@0600}];
            [fm createFileAtPath:err contents:nil attributes:@{NSFilePosixPermissions:@0600}];
            outHandle=[NSFileHandle fileHandleForWritingAtPath:out]; errHandle=[NSFileHandle fileHandleForWritingAtPath:err];
            if (!outHandle || !errHandle) error=[NSError errorWithDomain:@"MetadataDel" code:2 userInfo:@{NSLocalizedDescriptionKey:@"Не удалось подготовить обработку."}];
        }
        if (!error) {
            NSTask *task=[NSTask new]; task.executableURL=self.executableURL;
            task.arguments=@[@"--gui-clean", backup ? @"--backup=on" : @"--backup=off", @"--", url.path];
            task.standardOutput=outHandle; task.standardError=errHandle;
            self.currentTask=task;
            if ([task launchAndReturnError:&error]) {
                [task waitUntilExit];
                NSData *data=nil;
                if ([[fm attributesOfItemAtPath:out error:nil] fileSize] <= 1024*1024) data=[NSData dataWithContentsOfFile:out];
                id parsed=data ? [NSJSONSerialization JSONObjectWithData:data options:0 error:nil] : nil;
                if ([parsed isKindOfClass:NSDictionary.class]) result=parsed;
                // Schema 2: "incomplete" (written, but metadata remains; exit 1) and the private "backupPath".
                id success=result[@"success"], incomplete=result[@"incomplete"], backupPath=result[@"backupPath"];
                BOOL valid=result && [result[@"schemaVersion"] isEqual:@2] && [result[@"inputPath"] isEqual:url.path]
                    && MDIsBoolean(success) && MDIsBoolean(incomplete) && MDIsOptionalString(result[@"message"])
                    && MDIsOptionalString(backupPath) && (![backupPath isKindOfClass:NSString.class] || [backupPath isAbsolutePath]);
                if (valid && [success boolValue]) valid=task.terminationStatus==([incomplete boolValue] ? 1 : 0) && [result[@"outputPath"] isKindOfClass:NSString.class] && [result[@"outputPath"] isAbsolutePath];
                else if (valid) valid=task.terminationStatus==2 && result[@"outputPath"]==NSNull.null && ![incomplete boolValue];
                if (!valid || task.terminationReason != NSTaskTerminationReasonExit) {
                    result=nil; error=[NSError errorWithDomain:@"MetadataDel" code:2 userInfo:@{NSLocalizedDescriptionKey:@"Обработчик не подтвердил результат. Проверьте файл перед повторной очисткой."}];
                }
            }
        }
        self.currentTask=nil; [outHandle closeFile]; [errHandle closeFile]; [fm removeItemAtPath:dir error:nil];
        dispatch_async(dispatch_get_main_queue(), ^{ completion(result,error); });
    });
}
- (void)cancel { NSTask *task=self.currentTask; if (task.running) [task terminate]; }
@end
