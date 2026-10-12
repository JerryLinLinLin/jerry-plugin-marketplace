In this tutorial we show how to send messages to and from a target process.

## Setting up the experiment

Create a file `hello.c`:

```c
#include <stdio.h>
#include <unistd.h>

void
f (int n)
{
  printf ("Number: %d\n", n);
}

int
main (int argc,
      char * argv[])
{
  int i = 0;

  printf ("f() is at %p\n", f);

  while (1)
  {
    f (i++);
    sleep (1);
  }
}
```

Compile with:

```bash
$ gcc -Wall hello.c -o hello
```

Start the program and make note of the address of `f()` (`0x400544` in the
following example):

```bash
f() is at 0x400544
Number: 0
Number: 1
Number: 2
…
```

## Sending messages from a target process

The following script shows how to send a message back to the Python process.
You can send any JavaScript value which is serializable to JSON.

Create a file `send.py` containing:

```py
import frida
import sys

session = frida.attach("hello")
script = session.create_script("send(1337);")
def on_message(message, data):
    print(message)
script.on('message', on_message)
script.load()
sys.stdin.read()
```

When you run this script:

```bash
$ python send.py
```

it should print the following message:

```py
{'type': 'send', 'payload': 1337}
```

This means that the JavaScript code `send(1337)` has been executed inside the
`hello` process. Terminate the script with `Ctrl-D`.

### Handling runtime errors from JavaScript

If the JavaScript script throws an uncaught exception, this will be propagated
from the target process into the Python script. If you replace `send(1337)`
with `send(a)` (an undefined variable), the following message will be received
by Python:

```py
{'type': 'error', 'description': 'ReferenceError: a is not defined', 'lineNumber': 1}
```

Note the `type` field (`error` vs `send`).

## Receiving messages in a target process

It is possible to send messages from the Python script to the JavaScript
script. Create the file `pingpong.py`:

```py
import frida
import sys

session = frida.attach("hello")
script = session.create_script("""
    recv('poke', function onMessage(pokeMessage) { send('pokeBack'); });
""")
def on_message(message, data):
    print(message)
script.on('message', on_message)
script.load()
script.post({"type": "poke"})
sys.stdin.read()
```

Running the script:

```bash
$ python pingpong.py
```

produces the output:

```py
{'type': 'send', 'payload': 'pokeBack'}
```

<div class="note info">
  <h5>The mechanics of recv()</h5>
  <p>
    The recv() method itself is async (non-blocking). The registered callback
    (onMessage) will receive exactly one message. To receive the next message,
    the callback must be reregistered with recv().
  </p>
</div>

### Blocking receives in the target process

It is possible to wait for a message to arrive (a blocking receive) inside your
JavaScript script. Create a script `rpc.py`:

```py
import frida
import sys

session = frida.attach("hello")
script = session.create_script("""
Interceptor.attach(ptr("%s"), {
    onEnter(args) {
        send(args[0].toString());
        const op = recv('input', value => {
            args[0] = ptr(value.payload);
        });
        op.wait();
    }
});
""" % int(sys.argv[1], 16))
def on_message(message, data):
    print(message)
    val = int(message['payload'], 16)
    script.post({'type': 'input', 'payload': str(val * 2)})
script.on('message', on_message)
script.load()
sys.stdin.read()
```

The program `hello` should be running, and you should make a note of the address
printed at its beginning (e.g. `0x400544`). Run:

```bash
$ python rpc.py 0x400544
```

then observe the change in the terminal where `hello` is running:

```bash
Number: 3
Number: 8
Number: 10
Number: 12
Number: 14
Number: 16
Number: 18
Number: 20
Number: 22
Number: 24
Number: 26
Number: 14
```

The `hello` program should start writing "doubled" values until you stop the
Python script (`Ctrl-D`).
