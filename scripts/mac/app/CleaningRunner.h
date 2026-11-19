#import <Foundation/Foundation.h>
@interface CleaningRunner : NSObject
- (instancetype)initWithExecutableURL:(NSURL *)url;
- (void)cleanFileURL:(NSURL *)url backup:(BOOL)backup completion:(void (^)(NSDictionary *, NSError *))completion;
/// Asks the running worker to stop (SIGTERM): it removes its working copy and leaves the original intact.
- (void)cancel;
@end
