#import <Foundation/Foundation.h>
@interface IntegrationManager : NSObject
+ (BOOL)isInstalled;
+ (BOOL)isInstalledAtHome:(NSString *)home;
+ (BOOL)needsUpdate;
+ (void)installWithCompletion:(void (^)(NSError *))completion;
+ (void)removeWithCompletion:(void (^)(NSError *))completion;
@end
