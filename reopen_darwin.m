#import <Cocoa/Cocoa.h>

// Implemented in Go (reopen_darwin.go); opens the config window.
void hearkenReopen(void);

@interface HearkenReopen : NSObject
@end

@implementation HearkenReopen
- (void)onReopen:(NSAppleEventDescriptor *)ev withReplyEvent:(NSAppleEventDescriptor *)reply
{
  hearkenReopen();
}
@end

void hearkenInstallReopenHandler(void)
{
  // AppKit installs its own 'rapp' handler in -[NSApplication finishLaunching];
  // replacing it is safe here because the daemon owns no windows for AppKit's
  // default reopen behaviour to act on. Must run on the main thread — systray
  // calls its Go onReady from a goroutine, not the AppKit thread.
  dispatch_async(dispatch_get_main_queue(), ^{
    static HearkenReopen *handler;
    handler = [[HearkenReopen alloc] init];
    [[NSAppleEventManager sharedAppleEventManager] setEventHandler:handler
                                                       andSelector:@selector(onReopen:withReplyEvent:)
                                                     forEventClass:kCoreEventClass
                                                        andEventID:kAEReopenApplication];
  });
}
