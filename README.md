AL-Redis (AngiesList.Redis)
---------------------------
Modifications for Cinchcast included

The AngiesList.Redis library contains a few useful classes:

* A high performance Session State HttpModule that can replace the built-in Session module
* A simple, bucketed Key-Value store  

<br />

### Redis Session State HttpModule (`AngiesList.Redis.RedisSessionStateModule`)

`RedisSessionStateModule` is a IHttpModule that can replace ASP.NET's default Session module. It has the following 
features/differences:

* Session data is stored in Redis (duh)
* This module does NOT do the per request locking that the default module does (see: http://msdn.microsoft.com/en-us/library/ms178587.aspx ),
  which means that multiple request under the same SessionId can be processed concurrently.
* Session items are stored and accessed independently from items in a Redis Hash. So when session is saved at the end 
  of a request, only the session items that were modified during that request need to be persisted to Redis.

To use with Integrated Pipeline mode:
Create a `remove` then an `add` in the `modules` element inside the `system.webServer` element in your web.config like so:

```xml
<modules>
  <remove name="Session" />
	<add name="Session" type="AngiesList.Redis.RedisSessionStateModule, AngiesList.Redis" />
</modules>
```

For IIS 6 or earlier or Classic Pipeline mode:
Do the same except in the `httpModules` element in the `system.web` element.  

<br />

### Bucketed Key-Value store (`AngiesList.Redis.KeyValueStore`)

Example usage:

```csharp
var tags = KeyValueStore.Bucket("tags");
tags.Set("redis", "The swiss army knife data structure server");

var description = tags.GetStringSync("redis");
```

You can optionally set an expiration (in seconds) when you use `Set`:

```csharp
KeyValueStore.Bucket("contentCache").Set("about_us", "We're awesome!", 600);
```

There are more getter methods:

* For binary data (GetRawSync() returns byte[])
* For whatever type T you want. GetSync<T> returns T and handles the (de)serialization from and to T for you.
* For callback based async operations, there are asyncronous version of all the Get_ methods. Example:

```csharp
bigNumbers.Get<Int64>("a_trillion", (num, exc) => {
  if (exc == null) {
    //do something with num
  }
});
```
  
<br />

### TODO:

*  <del>add locking support (like the SQL Server provider), make it optional</del> done.
*  add option to use different serializers
*  create a quick benchmark program

