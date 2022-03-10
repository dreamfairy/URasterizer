using System;
using System.Collections.Generic;
using UnityEngine;

namespace URasterizer
{    
    public class GPURasterizer : IRasterizer
    {
        int _width;
        int _height;

        RenderingConfig _config;

        Matrix4x4 _matModel;
        Matrix4x4 _matView;
        Matrix4x4 _matProjection;

        public Matrix4x4 ModelMatrix
        {
            get => _matModel;           
            set => _matModel = value;            
        }

        public Matrix4x4 ViewMatrix
        {
            get => _matView;
            set => _matView = value;
        }

        public Matrix4x4 ProjectionMatrix
        {
            get => _matProjection;
            set => _matProjection = value;
        }

        Color[] frame_buf;
        float[] depth_buf;
        Color[] temp_buf;

        Color[] samplers_color_MSAA;
        bool[] samplers_mask_MSAA;
        float[] samplers_depth_MSAA;

        public Texture2D texture;

        public FragmentShader CurrentFragmentShader {get; set;}

        //Stats
        int _trianglesAll, _trianglesRendered;
        int _verticesAll;

        public OnRasterizerStatUpdate StatDelegate;

        //优化GC
        Vector4[] _tmpVector4s = new Vector4[3];
        Vector3[] _tmpVector3s = new Vector3[3];

        //Compute shader
        ComputeShader computeShader;
        int kernelVertexProcess;
        int vertexBufferId;
        int normalBufferId;
        int outBufferId;
        int matMVPId;
        int matModelId;


        public GPURasterizer(int w, int h, RenderingConfig config)
        {
            Debug.Log($"GPURasterizer screen size: {w}x{h}");

            _config = config;

            _width = w;
            _height = h;

            frame_buf = new Color[w * h];
            depth_buf = new float[w * h];
            temp_buf = new Color[w * h];

            texture = new Texture2D(w, h);
            texture.filterMode = FilterMode.Point;

            if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
            {
                AllocateMSAABuffers();
            }

            //init for compute shader
            computeShader = config.ComputeShader;
            kernelVertexProcess = computeShader.FindKernel("VertexProcess");
            vertexBufferId = Shader.PropertyToID("vertexBuffer");
            normalBufferId = Shader.PropertyToID("normalBuffer");
            outBufferId = Shader.PropertyToID("outBuffer");
            matMVPId = Shader.PropertyToID("matMVP");
            matModelId = Shader.PropertyToID("matModel");
        }

        void AllocateMSAABuffers()
        {
            int MSAALevel = (int)_config.MSAA;
            int bufSize = _width * _height * MSAALevel * MSAALevel;
            if(samplers_color_MSAA==null || samplers_color_MSAA.Length != bufSize)
            {
                samplers_color_MSAA = new Color[bufSize];
                samplers_mask_MSAA = new bool[bufSize];
                samplers_depth_MSAA = new float[bufSize];
            }            
        }

        public float Aspect
        {
            get
            {
                return (float)_width / _height;
            }
        }

        void FillArray<T>(T[] arr, T value)
        {
            int i = 0;
            if(arr.Length > 16)
            {
                do
                {
                    arr[i++] = value;
                } while (i < arr.Length);

                while( i + 16 < arr.Length)
                {
                    Array.Copy(arr, 0, arr, i, 16);
                    i += 16;
                }
            }
            while (i < arr.Length)
            {
                arr[i++] = value;
            }
        }

        public void Clear(BufferMask mask)
        {
            ProfileManager.BeginSample("Rasterizer.Clear");

            if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
            {
                AllocateMSAABuffers();
            }

            if ((mask & BufferMask.Color) == BufferMask.Color)
            {                
                FillArray(frame_buf, _config.ClearColor);
                if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
                {
                    FillArray(samplers_color_MSAA, _config.ClearColor);
                    FillArray(samplers_mask_MSAA, false);
                }
            }
            if((mask & BufferMask.Depth) == BufferMask.Depth)
            {
                FillArray(depth_buf, 0f);
                if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
                {
                    FillArray(samplers_depth_MSAA, 0f);
                }
            }

            _trianglesAll = _trianglesRendered = 0;
            _verticesAll = 0;

            ProfileManager.EndSample();
            
        }

        public void SetupViewProjectionMatrix(Camera camera)
        {
            //左手坐标系转右手坐标系,以下坐标和向量z取反
            var camPos = camera.transform.position;
            camPos.z *= -1; 
            var lookAt = camera.transform.forward;
            lookAt.z *= -1;
            var up = camera.transform.up;
            up.z *= -1;
            
            ViewMatrix = TransformTool.GetViewMatrix(camPos, lookAt, up);

            if (camera.orthographic)
            {
                float halfOrthHeight = camera.orthographicSize;
                float halfOrthWidth = halfOrthHeight * Aspect;
                float f = -camera.farClipPlane;
                float n = -camera.nearClipPlane;
                ProjectionMatrix = TransformTool.GetOrthographicProjectionMatrix(-halfOrthWidth, halfOrthWidth, -halfOrthHeight, halfOrthHeight, f, n);
            }
            else
            {
                ProjectionMatrix = TransformTool.GetPerspectiveProjectionMatrix(camera.fieldOfView, Aspect, camera.nearClipPlane, camera.farClipPlane);
            }
        }

        public void Draw(RenderingObject ro, Camera camera)
        {
            ProfileManager.BeginSample("Rasterizer.Draw");

            Mesh mesh = ro.mesh;

            SetupViewProjectionMatrix(camera);

            ModelMatrix = ro.GetModelMatrix();                      

            Matrix4x4 mvp = _matProjection * _matView * _matModel;
            Matrix4x4 normalMat = _matModel.inverse.transpose;

            _verticesAll += mesh.vertexCount;
            _trianglesAll += ro.MeshTriangles.Length / 3;
            

            //Unity模型本地坐标系也是左手系，需要转成我们使用的右手系
            //1. z轴反转
            //2. 三角形顶点环绕方向从顺时针改成逆时针


            /// ------------- Vertex Shader -------------------
            VSOutBuf[] vsOutput = ro.vsOutputBuffer;                   

            
            ProfileManager.BeginSample("Rasterizer.VertexShader GPU");                

            var shader = _config.ComputeShader;            
            shader.SetMatrix(matMVPId, mvp);
            shader.SetMatrix(matModelId, _matModel);
            shader.SetBuffer(kernelVertexProcess, vertexBufferId, ro.VertexBuffer);
            shader.SetBuffer(kernelVertexProcess, normalBufferId, ro.NormalBuffer);
            shader.SetBuffer(kernelVertexProcess, outBufferId, ro.OutBuffer);
            
            int groupCnt = Mathf.CeilToInt(mesh.vertexCount/768f);
            groupCnt = groupCnt==0? 1: groupCnt;
            shader.Dispatch(kernelVertexProcess, groupCnt, 1, 1);  
            
            ro.OutBuffer.GetData(vsOutput);      

            ProfileManager.EndSample();                                     
            
           
            
            ProfileManager.BeginSample("Rasterizer.PrimitiveAssembly");

            var indices = ro.MeshTriangles;
            for(int i=0; i< indices.Length; i+=3)
            {         
                /// -------------- Primitive Assembly -----------------

                //注意这儿对调了v0和v1的索引，因为原来的 0,1,2是顺时针的，对调后是 1,0,2是逆时针的
                //Unity Quard模型的两个三角形索引分别是 0,3,1,3,0,2 转换后为 3,0,1,0,3,2
                int idx0 = indices[i+1];
                int idx1 = indices[i]; 
                int idx2 = indices[i+2];  

                var v = _tmpVector4s;                                           
                
                v[0] = vsOutput[idx0].clipPos;
                v[1] = vsOutput[idx1].clipPos;
                v[2] = vsOutput[idx2].clipPos;                                  
                
                // ------ Clipping -------
                if (Clipped(_tmpVector4s))
                {
                    continue;
                }                

                // ------- Perspective division --------
                //clip space to NDC
                for (int k=0; k<3; k++)
                {
                    v[k].x /= v[k].w;
                    v[k].y /= v[k].w;
                    v[k].z /= v[k].w;                  
                }

                //backface culling
                if (_config.BackfaceCulling && !ro.DoubleSideRendering)
                {
                    Vector3 v0 = new Vector3(v[0].x, v[0].y, v[0].z);
                    Vector3 v1 = new Vector3(v[1].x, v[1].y, v[1].z);
                    Vector3 v2 = new Vector3(v[2].x, v[2].y, v[2].z);
                    Vector3 e01 = v1 - v0;
                    Vector3 e02 = v2 - v0;
                    Vector3 cross = Vector3.Cross(e01, e02);
                    if (cross.z < 0)
                    {
                        continue;
                    }
                }

                ++_trianglesRendered;

                // ------- Viewport Transform ----------
                //NDC to screen space
                for (int k = 0; k < 3; k++)
                {
                    var vec = v[k];
                    vec.x = 0.5f * _width * (vec.x + 1.0f);
                    vec.y = 0.5f * _height * (vec.y + 1.0f);

                    //在硬件渲染中，NDC的z值经过硬件的透视除法之后就直接写入到depth buffer了，如果要调整需要在投影矩阵中调整
                    //由于我们是软件渲染，所以可以在这里调整z值。                    

                    //GAMES101约定的NDC是右手坐标系，z值范围是[-1,1]，但n为1，f为-1，因此值越大越靠近n。                    
                    //为了可视化Depth buffer，将最终的z值从[-1,1]映射到[0,1]的范围，因此最终n为1, f为0。离n越近，深度值越大。                    
                    //由于远处的z值为0，因此clear时深度要清除为0，然后深度测试时，使用GREATER测试。
                    //(当然我们也可以在这儿反转z值，然后clear时使用float.MaxValue清除，并且深度测试时使用LESS_EQUAL测试)
                    //注意：这儿的z值调整并不是必要的，只是为了可视化时便于映射为颜色值。其实也可以在可视化的地方调整。
                    //但是这么调整后，正好和Unity在DirectX平台的Reverse z一样，让near plane附近的z值的浮点数精度提高。
                    vec.z = vec.z * 0.5f + 0.5f; 

                    v[k] = vec;
                }

                Triangle t = new Triangle();
                t.Vertex0.Position = v[0];
                t.Vertex1.Position = v[1];
                t.Vertex2.Position = v[2];                

                //set obj normal
                t.Vertex0.Normal = vsOutput[idx0].objectNormal;
                t.Vertex1.Normal = vsOutput[idx1].objectNormal;
                t.Vertex2.Normal = vsOutput[idx2].objectNormal;                

                if (ro.MeshUVs.Length > 0)
                {                    
                    t.Vertex0.Texcoord = ro.MeshUVs[idx0];
                    t.Vertex1.Texcoord = ro.MeshUVs[idx1];
                    t.Vertex2.Texcoord = ro.MeshUVs[idx2];                    
                }

                //设置顶点色,使用config中的颜色数组循环设置                
                if(_config.VertexColors != null && _config.VertexColors.Colors.Length > 0)
                {
                    int vertexColorCount = _config.VertexColors.Colors.Length;

                    t.Vertex0.Color = _config.VertexColors.Colors[idx0 % vertexColorCount];
                    t.Vertex1.Color = _config.VertexColors.Colors[idx1 % vertexColorCount];
                    t.Vertex2.Color = _config.VertexColors.Colors[idx2 % vertexColorCount];
                }
                else
                {
                    t.Vertex0.Color = Color.white;
                    t.Vertex1.Color = Color.white;
                    t.Vertex2.Color = Color.white;
                }

                //set world space pos & normal
                t.Vertex0.WorldPos = vsOutput[idx0].worldPos;
                t.Vertex1.WorldPos = vsOutput[idx1].worldPos;
                t.Vertex2.WorldPos = vsOutput[idx2].worldPos;
                t.Vertex0.WorldNormal = vsOutput[idx0].worldNormal;
                t.Vertex1.WorldNormal = vsOutput[idx1].worldNormal;
                t.Vertex2.WorldNormal = vsOutput[idx2].worldNormal;

                /// ---------- Rasterization -----------
                if (_config.WireframeMode)
                {                    
                    RasterizeWireframe(t);                    
                }
                else
                {                    
                    RasterizeTriangle(t, ro);                    
                }
                
            }

            ProfileManager.EndSample();

            //Resolve AA
            if(_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
            {
                int MSAALevel = (int)_config.MSAA;
                int SamplersPerPixel = MSAALevel * MSAALevel;

                for (int y=0; y < _height; ++y)
                {
                    for(int x=0; x < _width; ++x)
                    {
                        int index = GetIndex(x, y);
                        Color color = Color.clear;
                        float a = 0.0f;
                        for(int si=0; si < MSAALevel; ++si)
                        {
                            for(int sj=0; sj < MSAALevel; ++sj)
                            {
                                int xi = x * MSAALevel + si;
                                int yi = y * MSAALevel + sj;
                                int indexSamper = yi * _width * MSAALevel + xi;
                                if (samplers_mask_MSAA[indexSamper])
                                {
                                    color += samplers_color_MSAA[indexSamper];
                                    a += 1.0f;
                                }
                            }
                        }
                        if(a > 0.0f)
                        {
                            frame_buf[index] = color / SamplersPerPixel;
                        }
                    }
                }
            }

            ProfileManager.EndSample();
        }        

        //三角形Clipping操作，对于部分在clipping volume中的图元，
        //硬件实现时一般只对部分顶点z值在near,far之间的图元进行clipping操作，
        //而部分顶点x,y值在x,y裁剪平面之间的图元则不进行裁剪，只是通过一个比viewport更大一些的guard-band区域进行整体剔除（相当于放大x,y的测试范围）
        //这样x,y裁剪平面之间的图元最终在frame buffer上进行Scissor测试。
        //此处的实现简化为只整体剔除，不做任何clipping操作。对于x,y裁剪没问题，虽然没扩大region,也可以最后在frame buffer上裁剪掉。
        //对于z的裁剪由于没有处理，会看到整个三角形消失导致的边缘不齐整
        bool Clipped(Vector4[] v)
        {
            //Clip space使用GAMES101规范，右手坐标系，n为+1， f为-1
            //裁剪（仅整体剔除）     
            //实际的硬件是在Clip space裁剪，所以此处我们也使用clip space （当然由于我们不真正的裁剪，只是整体剔除，所以其实在NDC操作更方便）
            for (int i = 0; i < 3; ++i)
            {
                var vertex = v[i];
                var w = vertex.w;
                w = w >= 0 ? w : -w; //由于NDC中总是满足-1<=Zndc<=1, 而当 w < 0 时，-w >= Zclip = Zndc*w >= w。所以此时clip space的坐标范围是[w,-w], 为了比较时更明确，将w取正
                
                bool inside = (vertex.x <= w && vertex.x >= -w
                    && vertex.y <= w && vertex.y >= -w
                    && vertex.z <= w && vertex.z >= -w);
                if (inside)
                {             
                    //不裁剪三角形，只要有任意一点在clip space中则三角形整体保留
                    return false;
                }
            }

            //三个顶点都不在三角形中则剔除
            return true;
        }

        #region Wireframe mode
        //Breshham算法画线,颜色使用线性插值（非透视校正）
        private void DrawLine(Vector3 begin, Vector3 end, Color colorBegin, Color colorEnd)
        {            
            int x1 = Mathf.FloorToInt(begin.x);
            int y1 = Mathf.FloorToInt(begin.y);
            int x2 = Mathf.FloorToInt(end.x);
            int y2 = Mathf.FloorToInt(end.y);            

            int x, y, dx, dy, dx1, dy1, px, py, xe, ye, i;

            dx = x2 - x1;
            dy = y2 - y1;
            dx1 = Math.Abs(dx);
            dy1 = Math.Abs(dy);
            px = 2 * dy1 - dx1;
            py = 2 * dx1 - dy1;

            Color c1 = colorBegin;
            Color c2 = colorEnd;

            if (dy1 <= dx1)
            {
                if (dx >= 0)
                {
                    x = x1;
                    y = y1;
                    xe = x2;
                }
                else
                {
                    x = x2;
                    y = y2;
                    xe = x1;
                    c1 = colorEnd;
                    c2 = colorBegin;
                }
                Vector3 point = new Vector3(x, y, 1.0f);                 
                SetPixel(point, c1);
                for (i = 0; x < xe; i++)
                {
                    x++;
                    if (px < 0)
                    {
                        px += 2 * dy1;
                    }
                    else
                    {
                        if ((dx < 0 && dy < 0) || (dx > 0 && dy > 0))
                        {
                            y++;
                        }
                        else
                        {
                            y--;
                        }
                        px +=  2 * (dy1 - dx1);
                    }
                    
                    Vector3 pt = new Vector3(x, y, 1.0f);
                    float t = 1.0f - (float)(xe - x) / dx1;
                    Color line_color = Color.Lerp(c1, c2, t);                    
                    SetPixel(pt, line_color);
                }
            }
            else
            {
                if (dy >= 0)
                {
                    x = x1;
                    y = y1;
                    ye = y2;
                }
                else
                {
                    x = x2;
                    y = y2;
                    ye = y1;
                    c1 = colorEnd;
                    c2 = colorBegin;
                }
                Vector3 point = new Vector3(x, y, 1.0f);                
                SetPixel(point, c1);
                
                for (i = 0; y < ye; i++)
                {
                    y++;
                    if (py <= 0)
                    {
                        py += 2 * dx1;
                    }
                    else
                    {
                        if ((dx < 0 && dy < 0) || (dx > 0 && dy > 0))
                        {
                            x++;
                        }
                        else
                        {
                            x--;
                        }
                        py += 2 * (dx1 - dy1);
                    }
                    Vector3 pt = new Vector3(x, y, 1.0f);
                    float t = 1.0f - (float)(ye - y) / dy1;
                    Color line_color = Color.Lerp(c1, c2, t);
                    SetPixel(pt, line_color);
                }
            }
        }

        private void RasterizeWireframe(Triangle t)
        {
            ProfileManager.BeginSample("Rasterizer.RasterizeWireframe");
            DrawLine(t.Vertex0.Position, t.Vertex1.Position, t.Vertex0.Color, t.Vertex1.Color);
            DrawLine(t.Vertex1.Position, t.Vertex2.Position, t.Vertex1.Color, t.Vertex2.Color);
            DrawLine(t.Vertex2.Position, t.Vertex0.Position, t.Vertex2.Color, t.Vertex0.Color);
            ProfileManager.EndSample();
        }

        #endregion

        

        //Screen space  rasterization
        void RasterizeTriangle(Triangle t, RenderingObject ro)
        {
            ProfileManager.BeginSample("Rasterizer.RasterizeTriangle");
            var v = _tmpVector4s;
            v[0] = t.Vertex0.Position;
            v[1] = t.Vertex1.Position;
            v[2] = t.Vertex2.Position;            
            
            //Find out the bounding box of current triangle.
            float minX = v[0].x;
            float maxX = minX;
            float minY = v[0].y;
            float maxY = minY;

            for(int i=1; i<3; ++i)
            {
                float x = v[i].x;
                if(x < minX)
                {
                    minX = x;
                } else if(x > maxX)
                {
                    maxX = x;
                }
                float y = v[i].y;
                if(y < minY)
                {
                    minY = y;
                }else if(y > maxY)
                {
                    maxY = y;
                }
            }

            int minPX = Mathf.FloorToInt(minX);
            minPX = minPX < 0 ? 0 : minPX;
            int maxPX = Mathf.CeilToInt(maxX);
            maxPX = maxPX > _width ? _width : maxPX;
            int minPY = Mathf.FloorToInt(minY);
            minPY = minPY < 0 ? 0 : minPY;
            int maxPY = Mathf.CeilToInt(maxY);
            maxPY = maxPY > _height ? _height : maxPY;

            if(_config.MSAA == MSAALevel.Disabled)
            {                
                // 遍历当前三角形包围中的所有像素，判断当前像素是否在三角形中
                // 对于在三角形中的像素，使用重心坐标插值得到深度值，并使用z buffer进行深度测试和写入
                for(int y = minPY; y < maxPY; ++y)
                {
                    for(int x = minPX; x < maxPX; ++x)
                    {
                        //if(IsInsideTriangle(x, y, t)) //-->检测是否在三角形内比使用重心坐标检测要慢，因此先计算重心坐标，再检查3个坐标是否有小于0
                        {
                            //计算重心坐标
                            var c = ComputeBarycentric2D(x, y, t);
                            float alpha = c.x;
                            float beta = c.y;
                            float gamma = c.z;
                            if(alpha < 0 || beta < 0 || gamma < 0){                                
                                continue;
                            }
                            //透视校正插值，z为透视校正插值后的view space z值
                            float z = 1.0f / (alpha / v[0].w + beta / v[1].w + gamma / v[2].w);
                            //zp为透视校正插值后的screen space z值
                            float zp = (alpha * v[0].z / v[0].w + beta * v[1].z / v[1].w + gamma * v[2].z / v[2].w) * z;
                            
                            //深度测试(注意我们这儿的z值越大越靠近near plane，因此大值通过测试）
                            int index = GetIndex(x, y);
                            if(zp >= depth_buf[index])
                            {
                                depth_buf[index] = zp;
                                
                                //透视校正插值
                                ProfileManager.BeginSample("Rasterizer.RasterizeTriangle.AttributeInterpolation");
                                Color color_p = (alpha * t.Vertex0.Color / v[0].w + beta * t.Vertex1.Color / v[1].w + gamma * t.Vertex2.Color / v[2].w) * z;
                                Vector2 uv_p = (alpha * t.Vertex0.Texcoord / v[0].w + beta * t.Vertex1.Texcoord / v[1].w + gamma * t.Vertex2.Texcoord / v[2].w) * z;
                                Vector3 normal_p = (alpha * t.Vertex0.Normal / v[0].w + beta * t.Vertex1.Normal  / v[1].w + gamma * t.Vertex2.Normal  / v[2].w) * z;
                                Vector3 worldPos_p = (alpha * t.Vertex0.WorldPos / v[0].w + beta * t.Vertex1.WorldPos / v[1].w + gamma * t.Vertex2.WorldPos / v[2].w) * z;
                                Vector3 worldNormal_p = (alpha * t.Vertex0.WorldNormal / v[0].w + beta * t.Vertex1.WorldNormal / v[1].w + gamma * t.Vertex2.WorldNormal / v[2].w) * z;
                                ProfileManager.EndSample();

                                if (CurrentFragmentShader != null)
                                {
                                    FragmentShaderInputData input = new FragmentShaderInputData();
                                    input.Color = color_p;
                                    input.UV = uv_p;
                                    input.Texture = ro.texture;
                                    input.LocalNormal = normal_p;
                                    input.WorldPos = worldPos_p;
                                    input.WorldNormal = worldNormal_p;

                                    ProfileManager.BeginSample("Rasterizer.RasterizeTriangle.FragmentShader");
                                    frame_buf[index] = CurrentFragmentShader(input);
                                    ProfileManager.EndSample();
                                }
                                

                                
                            }
                        }                        
                    }
                }
            }
            else
            {
                int MSAALevel = (int)_config.MSAA;
                float sampler_dis = 1.0f / MSAALevel;
                float sampler_dis_half = sampler_dis * 0.5f;

                for (int y = minPY; y < maxPY; ++y)
                {
                    for (int x = minPX; x < maxPX; ++x)
                    {
                        //检查每个子像素是否在三角形内，如果在进行重心坐标插值和深度测试
                        for(int si=0; si<MSAALevel; ++si)
                        {
                            for(int sj=0; sj<MSAALevel; ++sj)
                            {
                                float offsetx = sampler_dis_half + si * sampler_dis;
                                float offsety = sampler_dis_half + sj * sampler_dis;
                                if (IsInsideTriangle(x, y, t, offsetx, offsety))
                                {
                                    //计算重心坐标
                                    var c = ComputeBarycentric2D(x+offsetx, y+offsety, t);
                                    float alpha = c.x;
                                    float beta = c.y;
                                    float gamma = c.z;
                                    //透视校正插值，z为透视校正插值后的view space z值
                                    float z = 1.0f / (alpha / v[0].w + beta / v[1].w + gamma / v[2].w);
                                    //zp为透视校正插值后的screen space z值
                                    float zp = (alpha * v[0].z / v[0].w + beta * v[1].z / v[1].w + gamma * v[2].z / v[2].w) * z;

                                    //深度测试(注意我们这儿的z值越大越靠近near plane，因此大值通过测试）                                    
                                    int xi = x * MSAALevel + si;
                                    int yi = y * MSAALevel + sj;
                                    int index = yi * _width * MSAALevel + xi;
                                    if (zp > samplers_depth_MSAA[index])
                                    {
                                        samplers_depth_MSAA[index] = zp;
                                        samplers_mask_MSAA[index] = true;

                                        //透视校正插值
                                        Color color_p = (alpha * t.Vertex0.Color / v[0].w + beta * t.Vertex1.Color / v[1].w + gamma * t.Vertex2.Color / v[2].w) * z;
                                        samplers_color_MSAA[index] = color_p;
                                    }
                                }
                            }
                        }
                        
                    }
                }
            }

            ProfileManager.EndSample();
        }

        bool IsInsideTriangle(int x, int y, Triangle t, float offsetX=0.5f, float offsetY=0.5f)
        {
            ProfileManager.BeginSample("Rasterizer.IsInsideTriangle");
            var v = _tmpVector3s;            
            v[0] = new Vector3(t.Vertex0.Position.x, t.Vertex0.Position.y, t.Vertex0.Position.z);
            v[1] = new Vector3(t.Vertex1.Position.x, t.Vertex1.Position.y, t.Vertex1.Position.z);
            v[2] = new Vector3(t.Vertex2.Position.x, t.Vertex2.Position.y, t.Vertex2.Position.z);            

            //当前像素中心位置p
            Vector3 p = new Vector3(x + offsetX, y + offsetY, 0);            
            
            Vector3 v0p = p - v[0]; v0p[2] = 0;
            Vector3 v01 = v[1] - v[0]; v01[2] = 0;
            Vector3 cross0p = Vector3.Cross(v0p, v01);

            Vector3 v1p = p - v[1]; v1p[2] = 0;
            Vector3 v12 = v[2] - v[1]; v12[2] = 0;
            Vector3 cross1p = Vector3.Cross(v1p, v12);

            if(cross0p.z * cross1p.z > 0)
            {
                Vector3 v2p = p - v[2]; v2p[2] = 0;
                Vector3 v20 = v[0] - v[2]; v20[2] = 0;
                Vector3 cross2p = Vector3.Cross(v2p, v20);
                if(cross2p.z * cross1p.z > 0)
                {
                    ProfileManager.EndSample();
                    return true;
                }
            }

            ProfileManager.EndSample();
            return false;
        }

        Vector3 ComputeBarycentric2D(float x, float y, Triangle t)
        {
            ProfileManager.BeginSample("Rasterizer.ComputeBarycentric2D");
            var v = _tmpVector4s;            
            v[0] = t.Vertex0.Position;
            v[1] = t.Vertex1.Position;
            v[2] = t.Vertex2.Position;
            
            float c1 = (x * (v[1].y - v[2].y) + (v[2].x - v[1].x) * y + v[1].x * v[2].y - v[2].x * v[1].y) / (v[0].x * (v[1].y - v[2].y) + (v[2].x - v[1].x) * v[0].y + v[1].x * v[2].y - v[2].x * v[1].y);
            float c2 = (x * (v[2].y - v[0].y) + (v[0].x - v[2].x) * y + v[2].x * v[0].y - v[0].x * v[2].y) / (v[1].x * (v[2].y - v[0].y) + (v[0].x - v[2].x) * v[1].y + v[2].x * v[0].y - v[0].x * v[2].y);
            float c3 = (x * (v[0].y - v[1].y) + (v[1].x - v[0].x) * y + v[0].x * v[1].y - v[1].x * v[0].y) / (v[2].x * (v[0].y - v[1].y) + (v[1].x - v[0].x) * v[2].y + v[0].x * v[1].y - v[1].x * v[0].y);
            
            ProfileManager.EndSample();
            return new Vector3(c1, c2, c3);
        }

        public int GetIndex(int x, int y)
        {
            return y * _width + x;
        }

        public void SetPixel(Vector3 point, Color color)
        {
            if(point.x < 0 || point.x >= _width || point.y < 0 || point.y >= _height)
            {
                return;
            }

            int idx = (int)point.y * _width + (int)point.x;
            frame_buf[idx] = color;
        }

        public void UpdateFrame()
        {
            ProfileManager.BeginSample("CameraRenderer.UpdateFrame");

            switch (_config.DisplayBuffer)
            {
                case DisplayBufferType.Color:
                    texture.SetPixels(frame_buf);
                    break;
                case DisplayBufferType.DepthRed:
                case DisplayBufferType.DepthGray:
                    for (int i = 0; i < depth_buf.Length; ++i)
                    {
                        //depth_buf中的值范围是[0,1]，且最近处为1，最远处为0。因此可视化后背景是黑色
                        float c = depth_buf[i]; 
                        if(_config.DisplayBuffer == DisplayBufferType.DepthRed)
                        {
                            temp_buf[i] = new Color(c, 0, 0);
                        }
                        else
                        {
                            temp_buf[i] = new Color(c, c, c);
                        }                        
                    }
                    texture.SetPixels(temp_buf);
                    break;
            }                                
            
            texture.Apply();

            if (StatDelegate != null)
            {
                StatDelegate(_verticesAll, _trianglesAll, _trianglesRendered);
            }

            ProfileManager.EndSample();
        }


    }
}