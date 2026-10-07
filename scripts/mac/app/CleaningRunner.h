#import <Foundation/Foundation.h>
@interface CleaningRunner : NSObject
- (instancetype)initWithExecutableURL:(NSURL *)url;
- (void)cleanFileURL:(NSURL *)url backup:(BOOL)backup completion:(void (^)(NSDictionary *, NSError *))completion;
@end
