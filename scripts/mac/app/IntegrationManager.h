#import <Foundation/Foundation.h>
typedef NS_ENUM(NSInteger, MDIntegrationState) { MDIntegrationOff, MDIntegrationOn, MDIntegrationStale, MDIntegrationOutsideApplications };
@interface IntegrationManager : NSObject
+ (BOOL)isInstalled;
+ (BOOL)isInstalledAtHome:(NSString *)home;
+ (BOOL)needsUpdate;
+ (BOOL)isInApplications;
+ (MDIntegrationState)state;
+ (void)installWithCompletion:(void (^)(NSError *))completion;
+ (void)removeWithCompletion:(void (^)(NSError *))completion;
@end
