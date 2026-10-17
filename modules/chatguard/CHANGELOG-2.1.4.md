# ChatGuard 2.1.4

- Release the admin dialog and hotkey registrations on shutdown.
- Ignore late network responses after dialog disposal.
- Release closed/replaced composers and ignore out-of-order replies.
- Handle network send failures during reconnect.
- Pass the original chat line to the Suite canonical presentation bridge so custom chat layouts do not duplicate moderation or include decorative prefixes/suffixes in the message body.
